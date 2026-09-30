using Rukari.CharacterVoice.Runtime;

internal static class AuthoringCompileCompatibilityTests
{
    public static void LegacySessionStillCompiles() => Check(
        Equals(AuthoringCompileCompatibility.InvokeBuildOnly(new Legacy()), "legacy"));

    public static void CurrentSessionNeverPublishesDuringDiagnosticCompile()
    {
        var session = new Current();
        Check(Equals(AuthoringCompileCompatibility.InvokeBuildOnly(session), "current"));
        Check(!session.Published);
    }

    public static void UnknownSignaturesAreRejected()
    {
        try { AuthoringCompileCompatibility.Resolve(typeof(Unknown)); }
        catch (MissingMethodException) { return; }
        throw new Exception("An unknown bool argument must not be assumed to mean publishToSaves.");
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Compile compatibility failed."); }
    public sealed class Legacy { public string Compile() => "legacy"; }
    public sealed class Current
    {
        public bool Published;
        public string Compile(bool publishToSaves = true) { Published = publishToSaves; return "current"; }
    }
    public sealed class Unknown { public void Compile(bool destructiveOperation) { } }
}
