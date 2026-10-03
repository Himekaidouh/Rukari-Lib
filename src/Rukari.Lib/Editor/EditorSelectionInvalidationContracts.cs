namespace Rukari.Lib.Editor;

/// <summary>Optional managed capability supplied by the existing editor document service.</summary>
public interface IEditorSelectionInvalidation
{
    /// <summary>Discard the observed selection token on the ready main thread, without reading native state.</summary>
    ModResult<bool> InvalidateSelection();
}
