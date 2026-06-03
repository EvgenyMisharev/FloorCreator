using Autodesk.Revit.DB;

namespace FloorCreator
{
    internal static class ElementIdCompat
    {
        public static bool IsValid(ElementId elementId)
        {
            return elementId != null && elementId != ElementId.InvalidElementId;
        }

        public static long? GetValue(ElementId elementId)
        {
            return IsValid(elementId) ? GetValueUnsafe(elementId) : (long?)null;
        }

        public static bool HasSameValue(ElementId first, ElementId second)
        {
            long? firstValue = GetValue(first);
            long? secondValue = GetValue(second);
            return firstValue.HasValue && secondValue.HasValue && firstValue.Value == secondValue.Value;
        }

        private static long GetValueUnsafe(ElementId elementId)
        {
#if REVIT_2024 || REVIT_2025 || REVIT_2026 || REVIT_2027
            return elementId.Value;
#else
            return elementId.IntegerValue;
#endif
        }
    }
}
