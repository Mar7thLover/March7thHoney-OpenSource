using March7thHoney.Data.Config.Scene;
using March7thHoney.Enums;
using March7thHoney.Enums.Task;

internal static class SceneConditionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var condition = new SavedValueLoadCondition
        {
            Conditions = [new SavedValueCondition { SavedValueName = "gate", Value = 1, Operation = CompareTypeEnum.Equal }]
        };
        check(!condition.IsTrue(new() { ["gate"] = 0 }), "false AND saved-value gate must not become default true");
        check(condition.IsTrue(new() { ["gate"] = 1 }), "matching AND saved-value gate");
        condition.Operation = OperationEnum.Or;
        check(!condition.IsTrue(new() { ["gate"] = 0 }), "false OR saved-value gate must not become default true");
        condition.Conditions.Add(new SavedValueCondition { SavedValueName = "other", Value = 2, Operation = CompareTypeEnum.Equal });
        check(condition.IsTrue(new() { ["gate"] = 0, ["other"] = 2 }), "one matching OR saved-value gate");
        condition.Operation = OperationEnum.And;
        check(!condition.IsTrue(new() { ["gate"] = 1, ["other"] = 0 }), "one failed AND gate blocks group");
        condition.Operation = OperationEnum.Not;
        check(condition.IsTrue(new() { ["gate"] = 0, ["other"] = 0 }), "NOT of all-false gates");
        check(!condition.IsTrue(new() { ["gate"] = 1, ["other"] = 0 }), "NOT of a matching gate");
        condition.Conditions.Clear();
        check(condition.IsTrue([]) && !condition.IsTrue([], false), "empty saved-value conditions preserve caller default");
    }
}
