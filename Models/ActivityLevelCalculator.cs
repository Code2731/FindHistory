namespace FindHistory.Models;

public static class ActivityLevelCalculator
{
    public static int Calculate(int count, int maximumCount)
    {
        if (count <= 0 || maximumCount <= 0)
        {
            return 0;
        }

        var relativeLevel = Math.Clamp(
            (int)Math.Ceiling(Math.Log(count + 1d) / Math.Log(maximumCount + 1d) * 4d),
            1,
            4);
        var absoluteLevel = count switch
        {
            < 4 => 1,
            < 10 => 2,
            < 25 => 3,
            _ => 4
        };

        // A small history should not make one or two opens look like peak activity.
        return Math.Min(relativeLevel, absoluteLevel);
    }
}
