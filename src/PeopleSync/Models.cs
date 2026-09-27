namespace PeopleSync;

public sealed class Address
{
    public string? Street { get; init; }
    public string? City { get; init; }
    public string? PostalCode { get; init; }
}

public sealed class Phone
{
    public string? Mobile { get; init; }
    public string? Landline { get; init; }
}

public sealed class FamilyMember
{
    public string Name { get; init; } = string.Empty;
    public string? Born { get; init; }
    public Phone? Phone { get; set; }
    public Address? Address { get; set; }
}

public sealed class Person
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string? Description { get; set; }
    public Phone? Phone { get; set; }
    public Address? Address { get; set; }
    public List<FamilyMember> FamilyMembers { get; } = [];
}

public sealed record SyncRunSummary(string Fingerprint, int ParsedPeople, int SentBatches, int SkippedBatches);
