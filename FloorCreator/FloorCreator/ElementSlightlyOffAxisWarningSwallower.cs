using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FloorCreator
{
    class ElementSlightlyOffAxisWarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {

            IList<FailureMessageAccessor> failuresList = failuresAccessor.GetFailureMessages();
            foreach (FailureMessageAccessor fa in failuresList)
            {
                if (BuiltInFailures.InaccurateFailures.InaccurateSketchLine == fa.GetFailureDefinitionId())
                {
                    failuresAccessor.DeleteWarning(fa);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }
}
