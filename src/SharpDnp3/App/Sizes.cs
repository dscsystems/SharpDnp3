// SharpDnp3 — a DNP3 (IEEE 1815-2012) implementation in C#.
// Copyright (C) 2026 Ricardo Olsen / DSC Systems
// Licensed under the GNU General Public License v3.0 or later.
//
// The size table itself is generated from Objects/Spec/dnp3_objects.yaml into
// Generated.Sizes.cs. This file holds only the lookup logic around it.
//
// The table lives in this namespace rather than being reached through
// SharpDnp3.Objects because the framing layer must not depend on the codecs:
// App defines the IObjectSizer interface, Objects implements the codecs, and
// both are generated from the same spec so there is still exactly one source
// of truth.

namespace SharpDnp3.App;

/// <summary>Resolves object sizes from the generated spec table.</summary>
internal static partial class ObjectSizing
{
    /// <summary>Packs a group and variation into the generated table's key.</summary>
    private static ushort Gv(byte group, byte variation) =>
        (ushort)((group << 8) | variation);

    /// <summary>The sizer used when a caller supplies none.</summary>
    public static IObjectSizer DefaultSizer { get; } = new SpecSizer();

    /// <summary>Reads sizes out of the generated spec table.</summary>
    internal sealed class SpecSizer : IObjectSizer
    {
        /// <inheritdoc/>
        public bool TrySizeBits(byte group, byte variation, out int bits)
        {
            if (LengthIsVariationGroups.Contains(group))
            {
                // For these groups the variation number *is* the octet length,
                // which makes them self-describing without a size prefix.
                // Variation zero means "any length" and appears only in
                // requests.
                bits = variation * 8;
                return true;
            }

            if (VariableGroups.Contains(group))
            {
                // Genuinely variable-length. Reporting unknown makes the parser
                // say so rather than guess, and pushes it onto the size-prefix
                // path.
                bits = 0;
                return false;
            }

            return GeneratedSizes.TryGetValue(Gv(group, variation), out bits);
        }

        /// <summary>
        /// Reports whether the sizer recognises a group and variation.
        /// </summary>
        public bool Known(byte group, byte variation) => TrySizeBits(group, variation, out _);
    }
}
