// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.

using SharpDnp3.App;
using SharpDnp3.Objects;

namespace SharpDnp3.Outstation;

/// <summary>
/// Configures one dataset prototype, descriptor or present value. Prototype
/// expansion and application-defined element types are the application's
/// responsibility.
/// </summary>
public sealed class DatasetObject
{
    /// <summary>The group: 85 (prototype), 86 (descriptor) or 87 (present value).</summary>
    public byte Group { get; set; }

    /// <summary>The variation.</summary>
    public byte Variation { get; set; }

    /// <summary>The dataset index.</summary>
    public ushort Index { get; set; }

    /// <summary>The object encoding, without its size prefix.</summary>
    public byte[] Data { get; set; } = [];
}

public sealed partial class Database
{
    private readonly Dictionary<(byte Group, byte Variation, ushort Index), byte[]> _datasets = [];
    private readonly Dictionary<ushort, Class> _datasetClasses = [];

    internal static bool DatasetVariationKnown(byte group, byte variation) => group switch
    {
        85 or 87 or 88 => variation == 1,
        86 => variation is >= 1 and <= 3,
        _ => false,
    };

    /// <summary>
    /// Stores a dataset object. For present values an event snapshot is queued
    /// when <paramref name="cls"/> is an event class. The data is copied so later
    /// application mutations cannot alter a stored value or an unconfirmed
    /// event.
    /// </summary>
    /// <exception cref="BadConfigException">The object is not a valid dataset object.</exception>
    public void UpdateDataset(DatasetObject v, Class cls = Class.None)
    {
        ArgumentNullException.ThrowIfNull(v);
        if (!DatasetVariationKnown(v.Group, v.Variation) || v.Group == 88 || v.Data.Length == 0 ||
            v.Data.Length > FreeFormat.MaxFreeFormatObject)
        {
            throw new BadConfigException();
        }

        if (v.Group == 86 && v.Variation == 2 && v.Data.Length != 1)
        {
            throw new BadConfigException();
        }

        if (v.Group == 85 || (v.Group == 86 && v.Variation == 1))
        {
            DatasetCodec.ParseDescriptor(v.Data);
        }

        lock (_gate)
        {
            var value = (byte[])v.Data.Clone();
            _datasets[(v.Group, v.Variation, v.Index)] = value;
            if (_datasetClasses.TryGetValue(v.Index, out var assigned))
            {
                cls = assigned;
            }

            var eventClass = cls & Class.Class123;
            if (v.Group == 87 && eventClass != Class.None)
            {
                RaiseWithClass(eventClass, new Event
                {
                    Type = PointType.Dataset,
                    Index = v.Index,
                    Variation = 1,
                    Dataset = value,
                });
            }
        }
    }

    internal bool TryGetDataset(byte group, byte variation, ushort index, out byte[] value)
    {
        lock (_gate)
        {
            return _datasets.TryGetValue((group, variation, index), out value!);
        }
    }

    private void AssignDatasetClass(Class cls, ushort start, ushort stop)
    {
        foreach (var key in _datasets.Keys)
        {
            if (key.Group == 87 && key.Index >= start && key.Index <= stop)
            {
                _datasetClasses[key.Index] = cls;
            }
        }
    }
}
