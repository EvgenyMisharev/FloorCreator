using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace FloorCreator
{
    class ElementSlightlyOffAxisWarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {

            IList<FailureMessageAccessor> failuresList = failuresAccessor.GetFailureMessages();
            foreach (FailureMessageAccessor fa in failuresList)
            {
                if (BuiltInFailures.GroupFailures.AtomViolationWhenOnePlaceInstance == fa.GetFailureDefinitionId())
                {
                    failuresAccessor.DeleteWarning(fa);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }
}
