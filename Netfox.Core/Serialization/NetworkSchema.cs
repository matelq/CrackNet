namespace Netfox.Core.Serialization;

/// <summary>Maps (subject, property) to a serializer, with a fallback. Port of schemas/network-schema.gd.</summary>
public sealed class NetworkSchema<TSubject, TProperty, TSerializer>
    where TSubject : class
    where TProperty : notnull
    where TSerializer : class
{
    private readonly Dictionary<TSubject, Dictionary<TProperty, TSerializer>> _serializers = new(ReferenceEqualityComparer.Instance);

    public TSerializer Fallback { get; }

    public NetworkSchema(TSerializer fallback)
    {
        Fallback = fallback;
    }

    public void Add(TSubject subject, TProperty property, TSerializer serializer)
    {
        if (!_serializers.TryGetValue(subject, out var props))
            _serializers[subject] = props = new Dictionary<TProperty, TSerializer>();
        props[property] = serializer;
    }

    public void Erase(TSubject subject, TProperty property)
    {
        if (!_serializers.TryGetValue(subject, out var props)) return;
        props.Remove(property);
        if (props.Count == 0) _serializers.Remove(subject);
    }

    public void EraseSubject(TSubject subject) => _serializers.Remove(subject);

    public TSerializer GetSerializer(TSubject subject, TProperty property)
        => _serializers.TryGetValue(subject, out var props) && props.TryGetValue(property, out var s) ? s : Fallback;
}
