using SnaffCore.Classifiers;
using SnaffCore.Concurrency;
using SnaffCore.Config;
using SnaffCore.FileScan;
using System;
using System.IO;
using System.Text.RegularExpressions;
using static SnaffCore.Config.Options;

namespace SnaffCore.TreeWalk
{
    public class TreeWalker
    {
        private BlockingMq Mq { get; set; }
        private BlockingStaticTaskScheduler FileTaskScheduler { get; set; }
        private BlockingStaticTaskScheduler TreeTaskScheduler { get; set; }
        private FileScanner FileScanner { get; set; }

        public TreeWalker()
        {
            Mq = BlockingMq.GetMq();

            FileTaskScheduler = SnaffCon.GetFileTaskScheduler();
            TreeTaskScheduler = SnaffCon.GetTreeTaskScheduler();
            FileScanner = SnaffCon.GetFileScanner();
        }

        private int SmbTimeoutMs { get { return MyOptions.SmbTimeoutSeconds * 1000; } }

        public void WalkTree(string currentDir)
        {
            // Walks a tree checking files and generating results as it goes.

            try
            {
                if (!TimeoutHelper.RunWithTimeout(() => Directory.Exists(currentDir), SmbTimeoutMs))
                {
                    return;
                }
            }
            catch (TimeoutException)
            {
                Mq.Trace("Timed out checking if directory exists: " + currentDir);
                return;
            }

            // SCCM ContentLib($)
            try
            {
                var currentDirInfo = new DirectoryInfo(currentDir);
                string currentDirectoryName = currentDirInfo.Name; // Remove paths, keep dirname only
                if (currentDirectoryName == @"SCCMContentLib" || currentDirectoryName == @"SCCMContentLib$")
                {
                    if (!MyOptions.ScanSccm)
                    {
                        Mq.Info("SCCM content library found but skipped (use -S to scan): " + currentDir);
                        return;
                    }
                    Mq.Info("SCCM content library found: " + currentDir);
                    string dataLibDir = Path.Combine(currentDir, "DataLib"); // As full path
                    try
                    {
                        if (!TimeoutHelper.RunWithTimeout(() => Directory.Exists(dataLibDir), SmbTimeoutMs))
                        {
                            Mq.Error("SCCM content library found but no DataLib found: " + dataLibDir);
                            return;
                        }
                    }
                    catch (TimeoutException)
                    {
                        Mq.Trace("Timed out checking SCCM DataLib: " + dataLibDir);
                        return;
                    }
                    Mq.Info("SCCM content library: Entering into datalib: " + dataLibDir);
                    WalkSccmTree(dataLibDir, currentDir); // With base path name
                    return;
                }
            }
            catch (Exception e)
            {
                Mq.Degub(e.ToString());
                //continue;
            }


            // Existing code:
            try
            {
                string[] files = TimeoutHelper.RunWithTimeout(() => Directory.GetFiles(currentDir), SmbTimeoutMs);
                // check if we actually like the files
                foreach (string file in files)
                {
                    FileTaskScheduler.New(() =>
                    {
                        try
                        {
                            FileScanner.ScanFile(file);
                        }
                        catch (Exception e)
                        {
                            Mq.Error("Exception in FileScanner task for file " + file);
                            Mq.Trace(e.ToString());
                        }
                    });
                }
            }
            catch (TimeoutException)
            {
                Mq.Trace("Timed out listing files in: " + currentDir);
            }
            catch (UnauthorizedAccessException)
            {
                //Mq.Trace(e.ToString());
                //continue;
            }
            catch (DirectoryNotFoundException)
            {
                //Mq.Trace(e.ToString());
                //continue;
            }
            catch (IOException)
            {
                //Mq.Trace(e.ToString());
                //continue;
            }
            catch (Exception e)
            {
                Mq.Degub(e.ToString());
                //continue;
            }

            try
            {
                string[] subDirs = TimeoutHelper.RunWithTimeout(() => Directory.GetDirectories(currentDir), SmbTimeoutMs);
                // Create a new treewalker task for each subdir.
                if (subDirs.Length >= 1)
                {

                    foreach (string dirStr in subDirs)
                    {
                        bool scanDir = true;
                        foreach (ClassifierRule classifier in MyOptions.DirClassifiers)
                        {
                            try
                            {
                                DirClassifier dirClassifier = new DirClassifier(classifier);
                                DirResult dirResult = dirClassifier.ClassifyDir(dirStr);

                                if (dirResult.ScanDir == false)
                                {
                                    scanDir = false;
                                    break;
                                }
                            }
                            catch (Exception e)
                            {
                                Mq.Trace(e.ToString());
                                continue;
                            }
                        }
                        if (scanDir == true)
                        {
                            TreeTaskScheduler.New(() =>
                            {
                                try
                                {
                                    WalkTree(dirStr);
                                }
                                catch (Exception e)
                                {
                                    Mq.Error("Exception in TreeWalker task for dir " + dirStr);
                                    Mq.Error(e.ToString());
                                }
                            });
                        }
                        else
                        {
                            Mq.Trace("Skipped scanning on " + dirStr + " due to Discard rule match.");
                        }
                    }
                }
            }
            catch (TimeoutException)
            {
                Mq.Trace("Timed out listing subdirectories in: " + currentDir);
            }
            catch (UnauthorizedAccessException)
            {
                //Mq.Trace(e.ToString());
                //continue;
            }
            catch (DirectoryNotFoundException)
            {
                //Mq.Trace(e.ToString());
                //continue;
            }
            catch (IOException)
            {
                //Mq.Trace(e.ToString());
                //continue;
            }
            catch (Exception e)
            {
                Mq.Trace(e.ToString());
                //continue;
            }
        }
        public void WalkSccmTree(string currentDir, string sccmBaseDir)
        {
            // Walks a tree checking files and generating results as it goes.
            try
            {
                if (!TimeoutHelper.RunWithTimeout(() => Directory.Exists(currentDir), SmbTimeoutMs))
                {
                    return;
                }
            }
            catch (TimeoutException)
            {
                Mq.Trace("Timed out checking if SCCM directory exists: " + currentDir);
                return;
            }

            try
            {
                string[] files = TimeoutHelper.RunWithTimeout(() => Directory.GetFiles(currentDir), SmbTimeoutMs);
                // Process INI files inline on TreeWalker thread to avoid nested
                // FileTaskScheduler deadlock. Only the final ScanFile is queued to FileScanner.
                foreach (string file in files)
                {
                    try
                    {
                        FileInfo fileInfo = new FileInfo(file);

                        // Check if INI
                        if (fileInfo.Extension != ".INI")
                        {
                            Mq.Trace("Skipping non-INI in DataLib: " + fileInfo.FullName);
                            continue;
                        }

                        // Check if it points to real file
                        string fileString = TimeoutHelper.RunWithTimeout(() => File.ReadAllText(fileInfo.FullName), SmbTimeoutMs);
                        if (!fileString.StartsWith(@"[File]"))
                        {
                            Mq.Trace("Skipping non-file-pointer INI in DataLib: " + fileInfo.FullName);
                            continue;
                        }

                        // Obtain hash
                        string pattern = @"Hash=([0-9A-Fa-f]+)";
                        if (!Regex.IsMatch(fileString, pattern))
                        {
                            Mq.Trace("No hash in DataLib INI: " + fileInfo.FullName);
                            continue;
                        }
                        string hashValueText = Regex.Match(fileString, pattern).Groups[1].Value;
                        string targetDirName = hashValueText.Substring(0, 4);

                        // strip off .INI to get actual name
                        string alternativeFullFileName = fileInfo.FullName.Substring(0, (fileInfo.FullName.Length - 4)); // Remove ".INI"
                        AlternativeFileInfo altFileInfo = new AlternativeFileInfo(alternativeFullFileName);

                        // Calculate real path
                        string targetFilePathName = Path.Combine(sccmBaseDir, @"FileLib", targetDirName, hashValueText);

                        Mq.Trace("SCCM: [" + targetFilePathName + "] via [" + fileInfo.FullName + "] as [" + alternativeFullFileName + "]");

                        // Queue only the final scan to FileScanner — no nesting
                        string tfpn = targetFilePathName;
                        AlternativeFileInfo afi = altFileInfo;
                        FileTaskScheduler.New(() =>
                        {
                            try
                            {
                                FileScanner.ScanFile(tfpn, afi);
                            }
                            catch (Exception e)
                            {
                                Mq.Error("Exception in FileScanner task for SCCM file " + tfpn);
                                Mq.Trace(e.ToString());
                            }
                        });
                    }
                    catch (TimeoutException)
                    {
                        Mq.Trace("Timed out reading SCCM INI: " + file);
                    }
                    catch (FileNotFoundException e)
                    {
                        Mq.Trace(e.ToString());
                    }
                    catch (UnauthorizedAccessException e)
                    {
                        Mq.Trace(e.ToString());
                    }
                    catch (PathTooLongException)
                    {
                        Mq.Trace(file + " path was too long for me to look at.");
                    }
                    catch (Exception e)
                    {
                        Mq.Trace(e.ToString());
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                //Mq.Trace(e.ToString());
            }
            catch (DirectoryNotFoundException)
            {
                //Mq.Trace(e.ToString());
            }
            catch (IOException)
            {
                //Mq.Trace(e.ToString());
            }
            catch (TimeoutException)
            {
                Mq.Trace("Timed out listing files in SCCM dir: " + currentDir);
            }
            catch (Exception e)
            {
                Mq.Degub(e.ToString());
            }

            try
            {
                string[] subDirs = TimeoutHelper.RunWithTimeout(() => Directory.GetDirectories(currentDir), SmbTimeoutMs);
                // Create a new treewalker task for each subdir.
                if (subDirs.Length >= 1)
                {

                    foreach (string dirStr in subDirs)
                    {
                        //foreach (ClassifierRule classifier in MyOptions.DirClassifiers) // No rules should be applied for SCCMContentLib($)

                        bool scanDir = true;
                        foreach (ClassifierRule classifier in MyOptions.DirClassifiers)
                        {
                            try
                            {
                                DirClassifier dirClassifier = new DirClassifier(classifier);
                                DirResult dirResult = dirClassifier.ClassifyDir(dirStr);

                                if (dirResult.ScanDir == false)
                                {
                                    scanDir = false;
                                    break;
                                }
                            }
                            catch (Exception e)
                            {
                                Mq.Trace(e.ToString());
                                continue;
                            }
                        }
                        if (scanDir == true)
                        {
                            TreeTaskScheduler.New(() =>
                            {
                                try
                                {
                                    WalkSccmTree(dirStr, sccmBaseDir);
                                }
                                catch (Exception e)
                                {
                                    Mq.Error("Exception in TreeWalker task for dir " + dirStr);
                                    Mq.Error(e.ToString());
                                }
                            });
                        }
                        else
                        {
                            Mq.Trace("Skipped scanning on " + dirStr + " due to Discard rule match.");
                        }

                    }
                }
            }
            catch (TimeoutException)
            {
                Mq.Trace("Timed out listing subdirectories in SCCM dir: " + currentDir);
            }
            catch (UnauthorizedAccessException)
            {
                //Mq.Trace(e.ToString());
                //continue;
            }
            catch (DirectoryNotFoundException)
            {
                //Mq.Trace(e.ToString());
                //continue;
            }
            catch (IOException)
            {
                //Mq.Trace(e.ToString());
                //continue;
            }
            catch (Exception e)
            {
                Mq.Trace(e.ToString());
                //continue;
            }
        }
    }
}