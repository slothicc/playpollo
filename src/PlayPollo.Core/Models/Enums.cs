namespace PlayPollo.Models
{
    public enum ServerKind
    {
        Auto,
        Apollo,
        Sunshine
    }

    public enum LaunchMode
    {
        /// <summary>Start the game through the playnite:// URI, so any library or emulator works.</summary>
        Uri,

        /// <summary>Run the game's own executable, so the server can track the process.</summary>
        Executable
    }
}
