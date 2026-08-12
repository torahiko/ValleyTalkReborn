namespace ValleytalkReborn;

public class ChildDescription
{
    public ChildDescription(string name, bool isMale, int age)
    {
        Name = name;
        IsMale = isMale;
        Age = age;
    }

    public string Name { get; }
    public bool IsMale { get; }
    public int Age { get; }
}