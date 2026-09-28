internal static class StartupHook
{
    public static void Initialize()
    {
        Environment.SetEnvironmentVariable("WPFSTUDIO_TEST_EXISTING_HOOK_RAN", "yes");
        int.TryParse(Environment.GetEnvironmentVariable("WPFSTUDIO_TEST_EXISTING_HOOK_COUNT"), out var count);
        Environment.SetEnvironmentVariable("WPFSTUDIO_TEST_EXISTING_HOOK_COUNT", (count + 1).ToString());
    }
}
