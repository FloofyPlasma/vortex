namespace VortexEngine.Core;

public readonly struct EntityId : IEquatable<EntityId>
{
    private readonly uint id;

    public EntityId(uint id) => this.id = id;

    public static EntityId Invalid => new(uint.MaxValue);
    public bool IsValid => id != uint.MaxValue;

    public bool Equals(EntityId other) => id == other.id;
    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);
    public override int GetHashCode() => id.GetHashCode();
    public override string ToString() => $"Entity#{id}";

    public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);
    public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);
}
