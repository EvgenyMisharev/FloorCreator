using Autodesk.Revit.DB;

namespace FloorCreator
{
    public sealed class PhaseFilterSelectionItem
    {
        public PhaseFilterSelectionItem(ElementId phaseFilterId, string name)
        {
            PhaseFilterId = phaseFilterId;
            Name = name;
        }

        public ElementId PhaseFilterId { get; }

        public string Name { get; }
    }
}
