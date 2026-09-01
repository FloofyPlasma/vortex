using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace VortexEngine;

public readonly struct EntityId : IEquatable<EntityId>
{
    public int Value { get; }

    public EntityId(int value) => Value = value;

    public bool Equals(EntityId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => $"Entity({Value})";

    public static bool operator ==(EntityId left, EntityId right) => left.Equals(right);
    public static bool operator !=(EntityId left, EntityId right) => !left.Equals(right);
}

public interface IComponent
{
}

public sealed class World
{
    private int nextEntityId = 1;
    private readonly Dictionary<EntityId, Dictionary<Type, IComponent>> entities = [];

    public EntityId CreateEntity()
    {
        var id = new EntityId(nextEntityId++);
        entities[id] = new Dictionary<Type, IComponent>();
        return id;
    }

    public void DestroyEntity(EntityId id)
    {
        entities.Remove(id);
    }

    public void AddComponent<T>(EntityId id, T component) where T : IComponent
    {
        if (!entities.TryGetValue(id, out var components))
            throw new InvalidOperationException($"Entity {id} does not exist");

        components[typeof(T)] = component;
    }

    public bool RemoveComponent<T>(EntityId id) where T : IComponent
    {
        return entities.TryGetValue(id, out var components) && components.Remove(typeof(T));
    }

    public bool TryGetComponent<T>(EntityId id, out T? component) where T : IComponent
    {
        component = default;

        if (!entities.TryGetValue(id, out var components)) return false;

        if (!components.TryGetValue(typeof(T), out var c)) return false;

        component = (T?)c;
        return true;
    }

    public bool HasComponent<T>(EntityId id) where T : IComponent
    {
        return entities.TryGetValue(id, out var components) && components.ContainsKey(typeof(T));
    }

    public IEnumerable<EntityId> EntitiesWith<T>() where T : IComponent
    {
        foreach (var (id, component) in entities)
        {
            if (component.ContainsKey(typeof(T))) yield return id;
        }
    }

    public IEnumerable<EntityId> EntitiesWith<T1, T2>()
        where T1 : IComponent
        where T2 : IComponent
    {
        foreach (var (id, components) in entities)
        {
            if (components.ContainsKey(typeof(T1)) &&
                components.ContainsKey(typeof(T2)))
                yield return id;
        }
    }
}