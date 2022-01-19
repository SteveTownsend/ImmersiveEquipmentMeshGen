using System;
using System.Collections.Generic;
using Mutagen.Bethesda.Synthesis.Settings;

namespace AllGUD
{
    public class Diagnostics
    {
        private bool badFileName;

        [SynthesisSettingName("Log File")]
        [SynthesisTooltip("This must be a valid writable filename in a valid path on your computer. Leave blank to just get Console output.")]
        [SynthesisDescription("Name of log file for diagnostic output.")]
        public string LogFile { get; set; } = "";

        private Logger? _logger;
        public Logger logger
        {
            get
            {
                if (_logger == null)
                {
                    string effectiveFile = badFileName ? "" : LogFile;
                    _logger = new Logger(effectiveFile);
                    if (!string.IsNullOrEmpty(effectiveFile))
                    {
                        _logger.WriteLine("Recording progress in log file {0} as well as to console", effectiveFile);
                    }
                }
                return _logger;
            }
        }

        public List<string> GetConfigErrors()
        {
            List<string> errors = new List<string>();
            try
            {
                LogFile = Helper.EnsureOutputFileIsValid(LogFile);
            }
            catch (Exception e)
            {
                badFileName = true;
                errors.Add(e.GetBaseException().ToString());
            }
            return errors;
        }
    }
}
