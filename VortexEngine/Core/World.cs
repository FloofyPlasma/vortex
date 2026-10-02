namespace VortexEngine.Core;

public sealed class World
{
    private Dictionary<EntityId, Dictionary<Type, IComponent>> entities = new();
    private uint nextEntityId = 0;

    public EntityId CreateEntity()
    {
        var id = new EntityId(nextEntityId++);
        entities[id] = new Dictionary<Type, IComponent>();
        return id;
    }

    public void AddComponent<T>(EntityId entity, T component) where T : IComponent
    {
        if (!entities.TryGetValue(entity, out var components))
            throw new InvalidOperationException($"Entity {entity} does not exist");

        components[typeof(T)] = component;
    }

    public bool TryGetComponent<T>(EntityId entity, out T component) where T : IComponent
    {
        component = default!;

        if (!entities.TryGetValue(entity, out var components))
            return false;

        if (components.TryGetValue(typeof(T), out var comp))
        {
            component = (T)comp;
            return true;
        }

        return false;
    }

    public bool HasComponent<T>(EntityId entity) where T : IComponent
    {
        return TryGetComponent<T>(entity, out _);
    }

    public IEnumerable<EntityId> EntitiesWith(params Type[] componentTypes)
    {
        if (componentTypes.Length == 0) yield break;

        foreach (var (id, components) in entities)
        {
            if (componentTypes.All(t => components.ContainsKey(t)))
                yield return id;
        }
    }

    public void DestroyEntity(EntityId entity)
    {
        entities.Remove(entity);
    }

    public void Clear()
    {
        entities.Clear();
        nextEntityId = 0;
    }
}
