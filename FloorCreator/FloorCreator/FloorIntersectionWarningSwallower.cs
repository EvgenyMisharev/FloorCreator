using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            }
            return FailureProcessingResult.Continue;
        }
    }
}
