using System.Numerics;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>
/// A refit onto XERX Hefty Pecs+ wrote 3,221 NaN vertices (raykie RUFF Elastic Set): a body triangle with two coincident
/// corners made ClosestOnTriangle divide 0 by 0, and a NaN distance wins every nearest search it enters.
/// </summary>
public class DegenerateTriangleTests
{
    public static TheoryData<Vector3, Vector3, Vector3> Degenerate => new()
    {
        { Vector3.Zero, Vector3.Zero, new Vector3(1f, 0f, 0f) },                // a == b
        { Vector3.Zero, new Vector3(1f, 0f, 0f), Vector3.Zero },                // a == c
        { new Vector3(1f, 0f, 0f), Vector3.Zero, Vector3.Zero },                // b == c
        { Vector3.Zero, Vector3.Zero, Vector3.Zero },                           // a point
        { Vector3.Zero, new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f) },     // collinear
    };

    [Theory]
    [MemberData(nameof(Degenerate))]
    public void DegenerateTriangleGivesAFinitePointOnIt(Vector3 a, Vector3 b, Vector3 c)
    {
        foreach (var p in new[] { new Vector3(0.5f, 1f, 0f), new Vector3(-1f, -1f, 0.5f), new Vector3(3f, 0.2f, -0.1f) })
        {
            var q = BrushTransfer.ClosestOnTriangle(p, a, b, c, out float u, out float v, out float w);
            Assert.True(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z), $"{q} for {p}");
            Assert.True(float.IsFinite(u) && float.IsFinite(v) && float.IsFinite(w));
            Assert.True(Vector3.Distance(q, a * u + b * v + c * w) < 1e-5f);

            var m = MeshMath.ClosestOnTriangle(new Vec3(p.X, p.Y, p.Z), new Vec3(a.X, a.Y, a.Z),
                                               new Vec3(b.X, b.Y, b.Z), new Vec3(c.X, c.Y, c.Z));
            Assert.True(float.IsFinite(m.X) && float.IsFinite(m.Y) && float.IsFinite(m.Z), $"{m} for {p}");
        }
    }

    [Fact]
    public void CoincidentCornersGiveTheTrueNearestPoint()
    {
        // The segment 0..1 along x; the point above its middle.
        var q = BrushTransfer.ClosestOnTriangle(new Vector3(0.5f, 1f, 0f), Vector3.Zero, Vector3.Zero, Vector3.UnitX,
                                                out _, out _, out _);
        Assert.Equal(0.5f, q.X, 5);
        Assert.Equal(0f, q.Y, 5);

        var m = MeshMath.ClosestOnTriangle(new Vec3(0.5f, 1f, 0f), new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 0f),
                                           new Vec3(1f, 0f, 0f));
        Assert.Equal(0.5f, m.X, 5);
        Assert.Equal(0f, m.Y, 5);
    }
}
