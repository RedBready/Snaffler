using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SnaffCore.Concurrency
{
    public class ScanProgressTracker
    {
        private static ScanProgressTracker _instance;

        private readonly ConcurrentDictionary<string, byte> _enumeratedComputers =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, List<string>> _computerShares =
            new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, byte> _completedShares =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, byte> _completedPaths =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private readonly object _fileLock = new object();
        private readonly string _outputPath;

        private ScanProgressTracker(string outputPath, string resumeFromPath)
        {
            _outputPath = outputPath;

            if (!string.IsNullOrEmpty(resumeFromPath))
            {
                LoadResumeFile(resumeFromPath);
            }

            // Write header to new progress file
            lock (_fileLock)
            {
                File.AppendAllText(_outputPath, "# Snaffler Progress v1" + Environment.NewLine);
                File.AppendAllText(_outputPath, "# Started: " + DateTime.UtcNow.ToString("o") + Environment.NewLine);
                if (!string.IsNullOrEmpty(resumeFromPath))
                {
                    File.AppendAllText(_outputPath, "# ResumedFrom: " + resumeFromPath + Environment.NewLine);
                }
            }
        }

        public static void Initialize(string outputPath, string resumeFromPath = null)
        {
            _instance = new ScanProgressTracker(outputPath, resumeFromPath);
        }

        public static ScanProgressTracker GetInstance()
        {
            return _instance;
        }

        private void LoadResumeFile(string path)
        {
            BlockingMq Mq = BlockingMq.GetMq();

            if (!File.Exists(path))
            {
                Mq.Error("Resume file not found: " + path);
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception e)
            {
                Mq.Error("Failed to read resume file: " + e.Message);
                return;
            }

            bool previousRunCompleted = false;
            int loadedComputers = 0;
            int loadedShares = 0;
            int loadedPaths = 0;

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                string[] parts = line.Split(new[] { '|' }, StringSplitOptions.None);
                if (parts.Length < 1)
                    continue;

                string prefix = parts[0].Trim();

                switch (prefix)
                {
                    case "COMPUTER_ENUMERATED":
                        if (parts.Length >= 2)
                        {
                            string computer = parts[1];
                            _enumeratedComputers.TryAdd(computer, 0);
                            List<string> shares = new List<string>();
                            for (int i = 2; i < parts.Length; i++)
                            {
                                if (!string.IsNullOrWhiteSpace(parts[i]))
                                    shares.Add(parts[i]);
                            }
                            _computerShares[computer] = shares;
                            loadedComputers++;
                        }
                        break;

                    case "SHARE_DONE":
                        if (parts.Length >= 2)
                        {
                            _completedShares.TryAdd(parts[1], 0);
                            loadedShares++;
                        }
                        break;

                    case "PATH_DONE":
                        if (parts.Length >= 2)
                        {
                            _completedPaths.TryAdd(parts[1], 0);
                            loadedPaths++;
                        }
                        break;

                    case "COMPLETED":
                        previousRunCompleted = true;
                        break;

                    default:
                        Mq.Trace("Resume file: skipping unrecognized line: " + line);
                        break;
                }
            }

            if (previousRunCompleted)
            {
                Mq.Info("Warning: resume file indicates previous run completed successfully. All work may be skipped.");
            }

            Mq.Info(string.Format("Resume: loaded {0} enumerated computers, {1} completed shares, {2} completed paths from {3}",
                loadedComputers, loadedShares, loadedPaths, path));
        }

        public void RecordComputerEnumerated(string computer, List<string> shares)
        {
            _enumeratedComputers.TryAdd(computer, 0);
            _computerShares[computer] = shares;

            string line = "COMPUTER_ENUMERATED|" + computer;
            if (shares != null && shares.Count > 0)
            {
                line += "|" + string.Join("|", shares);
            }

            lock (_fileLock)
            {
                File.AppendAllText(_outputPath, line + Environment.NewLine);
            }
        }

        public void RecordShareDone(string sharePath)
        {
            _completedShares.TryAdd(sharePath, 0);

            lock (_fileLock)
            {
                File.AppendAllText(_outputPath, "SHARE_DONE|" + sharePath + Environment.NewLine);
            }
        }

        public void RecordPathDone(string path)
        {
            _completedPaths.TryAdd(path, 0);

            lock (_fileLock)
            {
                File.AppendAllText(_outputPath, "PATH_DONE|" + path + Environment.NewLine);
            }
        }

        public void RecordCompleted()
        {
            lock (_fileLock)
            {
                File.AppendAllText(_outputPath, "COMPLETED" + Environment.NewLine);
            }
        }

        public bool IsComputerEnumerated(string computer)
        {
            return _enumeratedComputers.ContainsKey(computer);
        }

        public bool IsShareDone(string sharePath)
        {
            return _completedShares.ContainsKey(sharePath);
        }

        public bool IsPathDone(string path)
        {
            return _completedPaths.ContainsKey(path);
        }

        public List<string> GetPendingShares()
        {
            List<string> pending = new List<string>();
            foreach (var kvp in _computerShares)
            {
                foreach (string share in kvp.Value)
                {
                    if (!_completedShares.ContainsKey(share))
                    {
                        pending.Add(share);
                    }
                }
            }
            return pending;
        }
    }
}
