using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace FloorCreator
{
    class FloorIntersectionWarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failuresList = failuresAccessor.GetFailureMessages();
            foreach (FailureMessageAccessor fa in failuresList)
            {
                if (BuiltInFailures.OverlapFailures.FloorsOverlap == fa.GetFailureDefinitionId())
                {
                    failuresAccessor.DeleteWarning(fa);
                }
                else if (BuiltInFailures.InaccurateFailures.InaccurateSketchLine == fa.GetFailureDefinitionId())
                {
                    failuresAccessor.DeleteWarning(fa);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }
}
