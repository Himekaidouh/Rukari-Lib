using Rukari.CharacterVoice.Core;

internal static class VoiceOverrideDiagnosticsTests
{
    internal static void RunAll()
    {
        string root = Path.Combine(Path.GetTempPath(), "rukari-voice-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "voices"));
        try
        {
            string manifest = Path.Combine(root, "manifest.json");
            string index = Path.Combine(root, "voices", "voices.txt");
            File.WriteAllText(manifest, "{\"VoiceOverrides\":[\"voices/keep.ogg\",\"voices/gone.ogg\",\"../outside.ogg\"],\"keep\":true}");
            File.WriteAllText(index, "keep => []\ngone => []\n");
            File.WriteAllBytes(Path.Combine(root, "voices", "keep.ogg"), new byte[] { 1 });
            byte[] before = File.ReadAllBytes(manifest), beforeIndex = File.ReadAllBytes(index);
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
            var result = VoiceOverrideDiagnostics.Inspect(root);
            if (result.Total != 2 || result.Present.Count != 1 || result.Missing.Single().RelativePath != "voices/gone.ogg")
                throw new Exception("Voice diagnostics must distinguish present/missing and reject outside paths.");
            VoiceOverrideDiagnostics.Inspect(root);
            if (!before.SequenceEqual(File.ReadAllBytes(manifest)) || !beforeIndex.SequenceEqual(File.ReadAllBytes(index))
                || !files.SequenceEqual(Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x)))
                throw new Exception("Read-only diagnostics must not edit or add any project files.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}