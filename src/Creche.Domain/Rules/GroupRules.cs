namespace Creche.Domain.Rules;

public enum CapacityCheck { Ok, Warn, Block }

public static class GroupRules
{
    public static int FreePlaces(int capacity, int occupied) => Math.Max(0, capacity - occupied);

    /// <summary>What happens when one more child is added to a group that already holds <paramref name="occupied"/>.</summary>
    public static CapacityCheck CheckCapacity(int capacity, int occupied, bool blockWhenFull)
    {
        if (occupied < capacity) return CapacityCheck.Ok;
        return blockWhenFull ? CapacityCheck.Block : CapacityCheck.Warn;
    }

    /// <summary>Throws when the group is full, unless the crèche only warns and the user confirmed.</summary>
    public static void EnsureCanAdd(string groupName, int capacity, int occupied, bool blockWhenFull, bool allowOverCapacity)
    {
        switch (CheckCapacity(capacity, occupied, blockWhenFull))
        {
            case CapacityCheck.Block:
                throw new BusinessRuleException("GroupFull", $"Group '{groupName}' is full ({capacity} places).", groupName, capacity);
            case CapacityCheck.Warn when !allowOverCapacity:
                throw new GroupFullWarningException(groupName, capacity);
        }
    }

    public static int AgeInMonths(DateOnly dateOfBirth, DateOnly onDate)
    {
        int months = (onDate.Year - dateOfBirth.Year) * 12 + onDate.Month - dateOfBirth.Month;
        if (onDate.Day < dateOfBirth.Day) months--;
        return Math.Max(0, months);
    }

    public static bool AgeFits(DateOnly dateOfBirth, DateOnly onDate, int minAgeMonths, int maxAgeMonths)
    {
        int age = AgeInMonths(dateOfBirth, onDate);
        return age >= minAgeMonths && (maxAgeMonths <= 0 || age <= maxAgeMonths);
    }
}
