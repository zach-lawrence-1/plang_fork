using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PLang.Attributes;
using PLang.Errors;
using PLang.Interfaces;
using PLang.Runtime;
using PLang.Utils;
using System.ComponentModel;
using System.Diagnostics;

namespace PLang.Modules.AssertModule
{
	[Description("Assert object/variable/text or other entity to be what is expected. For unit testing")]
	public class Program : BaseProgram
	{

		public Program()
		{
		}


		[Description("User can force the type of expectedValue and actualValue, it should be FullName type, e.g. System.Int64, System.Double, etc. By default the types are not set and the runtime will try to match them")]
		public async Task<IError?> Contains(object? contains, object? actualValue)
		{
			bool result = false;
			if (contains is string str1 && actualValue is string str2) {
				
				result = str2.Contains(str1);
				if (!result) return new AssertError($"The value does not contain '{contains}", contains, actualValue, goalStep);
				return null;
			}

			var strContains = TypeHelper.GetAsString(contains);
			var strActual = TypeHelper.GetAsString(actualValue);

			
			result = strActual.Contains(strContains);

			if (!result) return new AssertError($"The value does not contain value.", strContains, strActual, goalStep);
			return null;
		}



		[Description("User can force the type of expectedValue and actualValue, it should be FullName type, e.g. System.Int64, System.Double, etc. By default the types are not set and the runtime will try to match them")]
		public async Task<IError?> IsEqual(object? expectedValue, object? actualValue, string resultVariable = "assertResult", string? expectedValueType = null, string? actualValueType = null)
		{
			if (expectedValue is ObjectValue ov)
			{
				expectedValue = ov.Value;
			}
			if (actualValue is ObjectValue ov2)
			{
				actualValue = ov2.Value;
			}

			bool result = false;
			if (expectedValueType == null && actualValueType == null)
			{
				var conditionProgram = GetProgramModule<ConditionalModule.Program>();
				var condition = await conditionProgram.IsEqual(expectedValue, actualValue);
				if (condition.Error != null) return condition.Error;

				result = condition.Result as bool? ?? false;
			}
			else
			{

				if (expectedValueType != null)
				{
					expectedValue = Convert.ChangeType(expectedValue, Type.GetType(expectedValueType));
				}
				if (actualValueType != null)
				{
					actualValue = Convert.ChangeType(actualValue, Type.GetType(actualValueType));
				}

				if (expectedValue != null)
				{
					result = expectedValue.Equals(actualValue);
				}
			}

			if (result) {
				return null;
			}
			 
			return new AssertError("Comparison failed", expectedValue, actualValue, goalStep);
		}
	}
}
