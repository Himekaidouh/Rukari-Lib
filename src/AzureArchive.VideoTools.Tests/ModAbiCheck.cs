using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AzureArchive.VideoTools.Tests;

/// <summary>
/// Read-only binary-compatibility check for the already-deployed mod packages.
///
/// A shared library that changes its public surface can break a provider compiled against the previous build, and
/// that failure only appears when the game loads it. This walks every member reference a feature assembly takes on
/// the shared library and proves the same name and signature still exists in the deployed library, so the one
/// class of breakage a managed test cannot reach is checked before the game is started.
///
/// Names and signature blobs are compared byte for byte. A method definition and a member reference to it carry
/// the same signature blob (calling convention, parameter count, return type and parameter types), so equality is
/// exact and no signature has to be decoded or interpreted.
///
/// It reads metadata only: nothing is loaded, resolved or executed.
/// </summary>
internal static class ModAbiCheck
{
    internal static int Run(string modsRoot, string libraryPath)
    {
        if (!Directory.Exists(modsRoot))
        {
            Console.Error.WriteLine($"Mods directory not found: {modsRoot}");
            return 2;
        }
        if (!File.Exists(libraryPath))
        {
            Console.Error.WriteLine($"Shared library not found: {libraryPath}");
            return 2;
        }

        string libraryName = Path.GetFileNameWithoutExtension(libraryPath);
        var libraryMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var libraryTypes = new HashSet<string>(StringComparer.Ordinal);
        using (FileStream libraryStream = File.OpenRead(libraryPath))
        using (var library = new PEReader(libraryStream))
        {
            if (!library.HasMetadata)
            {
                Console.Error.WriteLine($"{libraryPath} carries no managed metadata.");
                return 2;
            }
            MetadataReader reader = library.GetMetadataReader();
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(handle);
                string ns = reader.GetString(type.Namespace);
                string name = reader.GetString(type.Name);
                string full = ns.Length == 0 ? name : ns + "." + name;
                libraryTypes.Add(full);
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                    Key(reader, full, reader.GetString(method.Name), method.Signature, libraryMembers);
                }
                foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
                {
                    FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                    Key(reader, full, reader.GetString(field.Name), field.Signature, libraryMembers);
                }
            }
        }

        int checkedReferences = 0;
        int checkedTypes = 0;
        var missing = new List<string>();
        var consumers = new List<string>();
        foreach (string file in Directory.EnumerateFiles(modsRoot, "*.dll", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string consumerName = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(consumerName, libraryName, StringComparison.OrdinalIgnoreCase)) continue;
            using FileStream stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            MetadataReader reader = pe.GetMetadataReader();
            if (!References(reader, libraryName)) continue;
            consumers.Add(Path.GetRelativePath(modsRoot, file));

            foreach (TypeReferenceHandle handle in reader.TypeReferences)
            {
                TypeReference reference = reader.GetTypeReference(handle);
                if (!ScopedTo(reader, reference.ResolutionScope, libraryName)) continue;
                string ns = reader.GetString(reference.Namespace);
                string name = reader.GetString(reference.Name);
                string full = ns.Length == 0 ? name : ns + "." + name;
                checkedTypes++;
                if (!libraryTypes.Contains(full)) missing.Add($"{consumerName}: type {full} is gone");
            }

            foreach (MemberReferenceHandle handle in reader.MemberReferences)
            {
                MemberReference reference = reader.GetMemberReference(handle);
                if (reference.Parent.Kind != HandleKind.TypeReference) continue;
                TypeReference parent = reader.GetTypeReference((TypeReferenceHandle)reference.Parent);
                if (!ScopedTo(reader, parent.ResolutionScope, libraryName)) continue;
                string typeName = reader.GetString(parent.Namespace);
                string parentName = reader.GetString(parent.Name);
                string owner = typeName.Length == 0 ? parentName : typeName + "." + parentName;
                string memberName = reader.GetString(reference.Name);
                checkedReferences++;
                Key(reader, owner, memberName, reference.Signature, libraryMembers);
                string key = MemberKey(memberName, reader.GetBlobBytes(reference.Signature));
                if (!libraryMembers.TryGetValue(owner, out HashSet<string>? members) || !members.Contains(key))
                    missing.Add($"{consumerName}: {owner}.{memberName} {Blob(reader, reference.Signature)} no longer matches");
            }
        }

        Console.WriteLine($"ABI check: library={Path.GetFileName(libraryPath)}");
        Console.WriteLine($"ABI check: consumers={string.Join(", ", consumers)}");
        Console.WriteLine($"ABI check: types={checkedTypes}; member references={checkedReferences}; missing={missing.Count}");
        foreach (string line in missing) Console.Error.WriteLine("MISSING " + line);
        return missing.Count == 0 ? 0 : 1;
    }

    private static void Key(MetadataReader reader, string owner, string name, BlobHandle signature,
        Dictionary<string, HashSet<string>> into)
    {
        if (!into.TryGetValue(owner, out HashSet<string>? members))
        {
            members = new HashSet<string>(StringComparer.Ordinal);
            into[owner] = members;
        }
        members.Add(MemberKey(name, reader.GetBlobBytes(signature)));
    }

    private static string MemberKey(string name, byte[] signature) =>
        name + "|" + Convert.ToHexString(signature);

    private static string Blob(MetadataReader reader, BlobHandle handle) =>
        Convert.ToHexString(reader.GetBlobBytes(handle));

    private static bool References(MetadataReader reader, string libraryName)
    {
        foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
        {
            if (string.Equals(reader.GetString(reader.GetAssemblyReference(handle).Name), libraryName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool ScopedTo(MetadataReader reader, EntityHandle scope, string libraryName)
    {
        if (scope.Kind != HandleKind.AssemblyReference) return false;
        AssemblyReference reference = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
        return string.Equals(reader.GetString(reference.Name), libraryName, StringComparison.Ordinal);
    }
}
