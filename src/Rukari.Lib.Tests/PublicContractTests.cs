using System.Reflection;

namespace Rukari.Lib.Tests;

internal static class PublicContractTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    {
        "UnityEngine", "Unity.", "Il2Cpp", "Assembly-CSharp", "BepInEx", "0Harmony", "Spine", "AzureArchive.VideoTools"
    };

    internal static void ApiAssemblyHasNoNativeOrFeatureDependencies()
    {
        var apiAssembly = typeof(IModRuntime).Assembly;
        foreach (var dependency in apiAssembly.GetReferencedAssemblies())
            AssertAllowedAssembly(dependency.Name ?? string.Empty, $"API assembly dependency {dependency.FullName}");
    }

    /// <summary>
    /// Adding an optional parameter to an interface method REPLACES its signature: every provider compiled
    /// against the old arity then fails at runtime with MissingMethodException, which already broke both
    /// feature mods once. A new capability must therefore arrive as an additional overload.
    /// </summary>
    internal static void ToolboxRegistrationKeepsItsOriginalArity()
    {
        var toolbox = typeof(Rukari.Lib.Tools.IToolboxService);
        var registrations = toolbox.GetMethods()
            .Where(m => m.Name == "RegisterPage")
            .Select(m => m.GetParameters())
            .ToArray();
        Check.True(registrations.Any(p => p.Length == 5),
            "IToolboxService must keep the five-parameter RegisterPage(string,string,string,Func,Action) so providers compiled against it keep loading.");
        Check.True(registrations.Any(p => p.Length == 6),
            "The rail icon must be an added six-parameter overload.");
        foreach (var parameters in registrations)
            foreach (var parameter in parameters)
                Check.True(!parameter.IsOptional,
                    $"RegisterPage parameter '{parameter.Name}' must not be optional; optional parameters change the method signature and break already-compiled providers.");
        // The two overloads must not be ambiguous for a caller that omits the icon.
        int five = 0;
        int six = 0;
        foreach (var parameters in registrations)
        {
            if (parameters.Length == 5) five++;
            else if (parameters.Length == 6) six++;
        }
        Check.Equal(1, five, "Exactly one five-parameter overload may exist.");
        Check.Equal(1, six, "Exactly one six-parameter overload may exist.");

        // A page that draws itself arrives as a separate entry point, so nothing about RegisterPage changes.
        var hosted = toolbox.GetMethods().Where(m => m.Name == "RegisterHostedPage").ToArray();
        Check.Equal(1, hosted.Length, "Exactly one hosted registration entry point may exist.");
        var hostedParameters = hosted[0].GetParameters();
        Check.Equal(5, hostedParameters.Length,
            "RegisterHostedPage(ownerId, pageId, title, content, iconId) must keep its arity.");
        foreach (var parameter in hostedParameters)
            Check.True(!parameter.IsOptional,
                $"RegisterHostedPage parameter '{parameter.Name}' must not be optional.");
        Check.True(hostedParameters[3].ParameterType == typeof(Rukari.Lib.Tools.IToolPanelContent),
            "The hosted entry point takes the shared content contract, not a snapshot.");

        // List navigation is an init-only addition, never a seventh constructor/deconstruction component.
        // Already compiled snapshot providers must retain both original signatures.
        var snapshot = typeof(Rukari.Lib.Tools.ToolPageSnapshot);
        Type[] originalSnapshotTypes =
        {
            typeof(string), typeof(IReadOnlyList<Rukari.Lib.Tools.ToolButton>),
            typeof(IReadOnlyList<Rukari.Lib.Tools.ToolListItem>), typeof(string), typeof(string), typeof(bool)
        };
        Check.True(snapshot.GetConstructor(originalSnapshotTypes) is not null,
            "ToolPageSnapshot must retain its six-parameter constructor for compiled snapshot providers.");
        var deconstruct = snapshot.GetMethod("Deconstruct", BindingFlags.Public | BindingFlags.Instance,
            binder: null, types: originalSnapshotTypes.Select(type => type.MakeByRefType()).ToArray(), modifiers: null);
        Check.True(deconstruct is not null && deconstruct.GetParameters().All(parameter => parameter.IsOut),
            "ToolPageSnapshot must retain its six-component Deconstruct method.");
    }

    internal static void HostedButtonStyleKeepsTheFrozenSurfaceSignature()
    {
        var surface = typeof(Rukari.Lib.Tools.IToolPanelSurface);
        var original = surface.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == "Button")
            .ToArray();
        Check.Equal(1, original.Length, "The frozen surface keeps exactly its original Button method.");
        var parameters = original[0].GetParameters();
        Check.Equal(5, parameters.Length, "The original Button arity must remain unchanged for compiled pages.");
        Check.True(parameters[3].ParameterType == typeof(bool) && parameters[4].ParameterType == typeof(bool),
            "The original enabled and highlighted arguments keep their positions and types.");

        var styled = typeof(Rukari.Lib.Tools.IToolPanelSurfaceStyledButtons)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Check.Equal(1, styled.Length, "Styled buttons arrive through a separate optional capability.");
        Check.Equal("StyledButton", styled[0].Name);
        var styledParameters = styled[0].GetParameters();
        Check.Equal(6, styledParameters.Length);
        Check.True(styledParameters[3].ParameterType == typeof(Rukari.Lib.Tools.ToolSurfaceStyle),
            "A page chooses a semantic style, not a native colour or texture.");
        foreach (var parameter in styledParameters)
            Check.True(!parameter.IsOptional, "The new contract must not depend on optional parameter arity.");
        Check.True(typeof(Rukari.Lib.Tools.IToolPanelSurfaceStyledButtons)
                .IsAssignableFrom(typeof(Rukari.Lib.Tools.ToolPanelBuilder)),
            "The real hosted surface must provide the optional styled-button capability.");
    }

    internal static void ToolWindowRegistrationIsASiblingOfTheFrozenToolbox()
    {
        // The toolbox contract is frozen: adding a member would break every provider compiled against it, and a
        // page that failed to implement one would die with a TypeLoadException. Windows therefore arrive as a
        // sibling interface that a mod asks for and falls back from.
        var toolbox = typeof(Rukari.Lib.Tools.IToolboxService);
        Check.True(!toolbox.GetMethods().Any(m => m.Name.Contains("Window", StringComparison.Ordinal)),
            "No window member may be added to the frozen toolbox interface.");
        Check.True(typeof(Rukari.Lib.Tools.IToolWindowService).IsInterface,
            "The window capability is an interface a provider can ask for.");

        var register = typeof(Rukari.Lib.Tools.IToolWindowService)
            .GetMethods().Where(m => m.Name == "RegisterWindow").ToArray();
        Check.Equal(1, register.Length, "Exactly one window registration entry point may exist.");
        var parameters = register[0].GetParameters();
        Check.Equal(5, parameters.Length,
            "RegisterWindow(ownerId, windowId, title, content, iconId) must keep its arity.");
        foreach (var parameter in parameters)
            Check.True(!parameter.IsOptional, $"RegisterWindow parameter '{parameter.Name}' must not be optional.");
        Check.True(parameters[3].ParameterType == typeof(Rukari.Lib.Tools.IToolPanelContent),
            "A window takes the same content contract as a drawer page, not a second kind of page.");

        // The page contract itself is what makes a page usable in either host, so it stays exactly two members.
        var content = typeof(Rukari.Lib.Tools.IToolPanelContent);
        var declared = content.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Check.Equal(2, declared.Length,
            "The frozen page contract keeps exactly PreferredHeight and Draw; new needs arrive as sibling surfaces.");
        Check.True(typeof(Rukari.Lib.Tools.IToolPanelSizing).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length == 1,
            "Sizing stays one optional member so an already compiled page keeps loading without it.");

        // One service object answers to both capabilities, and the window capability has its own name.
        Check.True(Rukari.Lib.Tools.ToolCapabilities.Windows != Rukari.Lib.Tools.ToolCapabilities.Toolbox
            && Rukari.Lib.Tools.ToolCapabilities.Windows.Length != 0,
            "The window capability needs its own published name.");
    }

    internal static void ExportedContractsDoNotExposeNativeOrFeatureTypes()
    {
        var apiAssembly = typeof(IModRuntime).Assembly;
        var visited = new HashSet<Type>();
        var exported = apiAssembly.GetExportedTypes();
        Check.True(exported.Any(type => type.Name.Contains("Voice", StringComparison.Ordinal)), "Voice consumer contracts must be included in the independent API.");

        foreach (var type in exported)
        {
            CheckType(type, visited, type.FullName ?? type.Name);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                CheckType(method.ReturnType, visited, $"{type.Name}.{method.Name} return");
                foreach (var parameter in method.GetParameters())
                    CheckType(parameter.ParameterType, visited, $"{type.Name}.{method.Name} parameter {parameter.Name}");
            }

            foreach (var constructor in type.GetConstructors())
                foreach (var parameter in constructor.GetParameters())
                    CheckType(parameter.ParameterType, visited, $"{type.Name} constructor parameter {parameter.Name}");

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                CheckType(field.FieldType, visited, $"{type.Name}.{field.Name}");
        }
    }

    private static void CheckType(Type type, HashSet<Type> visited, string location)
    {
        if (!visited.Add(type))
            return;
        if (type.IsGenericParameter)
        {
            foreach (var constraint in type.GetGenericParameterConstraints())
                CheckType(constraint, visited, location);
            return;
        }

        AssertAllowedAssembly(type.Assembly.GetName().Name ?? string.Empty, location);
        Check.True(type != typeof(IntPtr) && type != typeof(UIntPtr) && !type.IsPointer, $"Raw native handle exposed at {location}.");
        if (type.HasElementType)
            CheckType(type.GetElementType()!, visited, location);
        foreach (var argument in type.GetGenericArguments())
            CheckType(argument, visited, location);
        if (type.BaseType is not null)
            CheckType(type.BaseType, visited, location);
        foreach (var contract in type.GetInterfaces())
            CheckType(contract, visited, location);
    }

    private static void AssertAllowedAssembly(string assemblyName, string location)
    {
        Check.True(!ForbiddenAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)),
            $"Native or feature-specific type leaked into independent API at {location}: {assemblyName}.");
    }
}
