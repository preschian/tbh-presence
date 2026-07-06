namespace TbhCompanion
{
    // Edition flag. The presence-only build is compiled with /define:PRESENCE_ONLY
    // (no embedded plugin, no deploy, no auto-synthesis UI). The default build is
    // the full presence + auto-synthesis edition.
    static class Build
    {
#if PRESENCE_ONLY
        public const bool Synth = false;
        public const string Edition = "Presence";
#else
        public const bool Synth = true;
        public const string Edition = "Full";
#endif
    }
}
