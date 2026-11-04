namespace ADBToolsNew.Core
{
    public class AdbCommandResult
    {
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public int ExitCode { get; set; }

        public bool IsSuccess => ExitCode == 0;
    }
}