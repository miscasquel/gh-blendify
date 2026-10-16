// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 miscasquel

using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;

namespace Blendify
{
    /// <summary>A single wire in the document.</summary>
    internal readonly struct WireInfo
    {
        public WireInfo(IGH_Param source, IGH_Param target)
        {
            Source = source;
            Target = target;
        }

        public readonly IGH_Param Source;
        public readonly IGH_Param Target;
    }

    /// <summary>
    /// Wire geometry and document mutations.
    ///
    /// The wire curve mirrors GH_Painter.ConnectionPathBezier exactly (horizontal handles of
    /// length max(0.5 * |dx|, 0.75 * |dy|)), so hit tests and previews match what Grasshopper
    /// renders. Mutations mirror Grasshopper's own wire operations, including undo records.
    /// </summary>
    internal static class GhOps
    {
        /// <summary>Canvas-space search radius for wires; Grasshopper itself uses 14 for double-click.</summary>
        public const float WireHitRadius = 12f;

        /// <summary>Canvas-space search radius for parameter grips (Grasshopper default is 12).</summary>
        public const float GripHitRadius = 12f;

        private const int HitSampleCount = 14;
        private const int DrawSampleCount = 24;

        private static float WireOffset(PointF source, PointF target)
        {
            float dy = 0.75f * Math.Abs(target.Y - source.Y);
            float dx = 0.5f * Math.Abs(target.X - source.X);
            return Math.Max(dx, dy);
        }

        /// <summary>Sampled polyline of the wire between two canvas points.</summary>
        public static PointF[] WirePolyline(PointF source, PointF target, int samples = DrawSampleCount)
        {
            float offset = WireOffset(source, target);
            var control1 = new PointF(source.X + offset, source.Y);
            var control2 = new PointF(target.X - offset, target.Y);

            var points = new PointF[samples + 1];
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                float u = 1f - t;
                float uu = u * u;
                float tt = t * t;
                points[i] = new PointF(
                    uu * u * source.X + 3f * uu * t * control1.X + 3f * u * tt * control2.X + tt * t * target.X,
                    uu * u * source.Y + 3f * uu * t * control1.Y + 3f * u * tt * control2.Y + tt * t * target.Y);
            }
            return points;
        }

        /// <summary>The point of the wire between two parameters that lies nearest to a canvas point.</summary>
        public static PointF NearestPointOnWire(IGH_Param source, IGH_Param target, PointF point)
        {
            var curve = WirePolyline(source.Attributes.OutputGrip, target.Attributes.InputGrip, 48);
            var best = curve[0];
            float bestDistance = float.MaxValue;
            foreach (var candidate in curve)
            {
                float dx = candidate.X - point.X;
                float dy = candidate.Y - point.Y;
                float distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        public static IEnumerable<WireInfo> EnumerateWires(GH_Document document)
        {
            foreach (var obj in document.Objects)
            {
                switch (obj)
                {
                    case IGH_Component component:
                        foreach (var input in component.Params.Input)
                            foreach (var source in input.Sources)
                                yield return new WireInfo(source, input);
                        break;

                    case IGH_Param param:
                        foreach (var source in param.Sources)
                            yield return new WireInfo(source, param);
                        break;
                }
            }
        }

        /// <summary>All wires crossed by the given stroke (canvas coordinates).</summary>
        public static List<WireInfo> FindWiresAlong(GH_Document document, IReadOnlyList<PointF> stroke, float tolerance = 2f)
        {
            var result = new List<WireInfo>();
            if (stroke.Count == 0)
                return result;

            var strokeBounds = BoundsOf(stroke);
            strokeBounds.Inflate(tolerance + 1f, tolerance + 1f);

            foreach (var wire in EnumerateWires(document))
            {
                var sourceAttributes = wire.Source.Attributes;
                var targetAttributes = wire.Target.Attributes;
                if (sourceAttributes == null || targetAttributes == null)
                    continue;

                var gripA = sourceAttributes.OutputGrip;
                var gripB = targetAttributes.InputGrip;

                // The curve is contained in the hull of its four control points.
                float offset = WireOffset(gripA, gripB);
                var wireBounds = RectangleF.FromLTRB(
                    Math.Min(gripA.X, gripB.X) - offset,
                    Math.Min(gripA.Y, gripB.Y) - offset,
                    Math.Max(gripA.X, gripB.X) + offset,
                    Math.Max(gripA.Y, gripB.Y) + offset);
                if (!strokeBounds.IntersectsWith(wireBounds))
                    continue;

                if (WireIntersectsStroke(gripA, gripB, stroke, tolerance))
                    result.Add(wire);
            }
            return result;
        }

        /// <summary>Inserts a relay into an existing wire, mirroring Grasshopper's own double-click behaviour.</summary>
        public static GH_Relay InsertRelay(GH_Document document, IGH_Param source, IGH_Param target, PointF location)
        {
            var relay = new GH_Relay();
            relay.CreateAttributes();
            relay.Attributes.Pivot = location;
            relay.WireDisplay = target.WireDisplay;
            relay.ExpirePreview(false);
            document.AddObject(relay, false, int.MaxValue);

            var record = new GH_UndoRecord("Blendify: Insert Relay");
            record.AddAction(new GH_AddObjectAction(relay));
            record.AddAction(new GH_WireAction(relay));
            relay.AddSource(source);
            relay.ResolveDisplayName();
            record.AddAction(new GH_WireAction(target));
            target.ReplaceSource(source, relay);
            document.UndoUtil.RecordEvent(record);
            relay.ExpireSolution(true);
            return relay;
        }

        /// <summary>Removes the given wires in a single undo step.</summary>
        public static void CutWires(GH_Document document, List<WireInfo> wires)
        {
            if (wires.Count == 0)
                return;

            var record = new GH_UndoRecord("Blendify: Cut Wires");
            var recorded = new HashSet<IGH_Param>();
            foreach (var wire in wires)
            {
                if (recorded.Add(wire.Target))
                    record.AddAction(new GH_WireAction(wire.Target));
            }
            document.UndoUtil.RecordEvent(record);

            foreach (var wire in wires)
                wire.Target.RemoveSource(wire.Source);

            document.NewSolution(false);
        }

        /// <summary>
        /// Connects a newly created object to the socket a wire was pulled from.
        /// Mirrors Grasshopper's wire-drop semantics (replace sources) and undo handling.
        /// Returns false when the object has no compatible socket.
        /// </summary>
        public static bool ConnectToNewObject(GH_Document document, IGH_DocumentObject obj, IGH_Param dragged, bool draggedFromInput)
        {
            var candidate = FindCompatibleParam(obj, dragged, wantOutput: draggedFromInput);
            if (candidate == null)
                return false;

            // Pulling from an input needs a source on the new object, and vice versa.
            var target = draggedFromInput ? dragged : candidate;
            var source = draggedFromInput ? candidate : dragged;
            if (target == null || source == null || ReferenceEquals(target, source))
                return false;

            var record = new GH_UndoRecord("Blendify: Connect");
            record.AddAction(new GH_WireAction(target));
            document.UndoUtil.RecordEvent(record);

            target.RemoveAllSources();
            target.AddSource(source);
            document.NewSolution(false);
            return true;
        }

        private static IGH_Param FindCompatibleParam(IGH_DocumentObject obj, IGH_Param other, bool wantOutput)
        {
            var candidates = new List<IGH_Param>();
            if (obj is IGH_Param param)
            {
                var attributes = param.Attributes;
                bool hasGrip = attributes == null || (wantOutput ? attributes.HasOutputGrip : attributes.HasInputGrip);
                if (hasGrip)
                    candidates.Add(param);
            }
            else if (obj is IGH_Component component)
            {
                candidates.AddRange(wantOutput ? component.Params.Output : component.Params.Input);
            }

            IGH_Param best = null;
            int bestScore = int.MinValue;
            foreach (var candidate in candidates)
            {
                int score = CompatibilityScore(candidate, other);
                if (!wantOutput && candidate.Sources != null && candidate.Sources.Count > 0)
                    score -= 1; // Prefer sockets that are still free.
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best;
        }

        private static int CompatibilityScore(IGH_Param candidate, IGH_Param other)
        {
            var candidateType = candidate.Type;
            var otherType = other?.Type;
            if (candidateType == null || otherType == null)
                return 2;
            if (candidateType == otherType)
                return 4;
            if (candidateType == typeof(IGH_Goo) || otherType == typeof(IGH_Goo))
                return 3;
            if (candidateType.IsAssignableFrom(otherType) || otherType.IsAssignableFrom(candidateType))
                return 2;
            return 1; // Grasshopper itself allows any connection; keep this as a last resort.
        }

        private static bool WireIntersectsStroke(PointF gripA, PointF gripB, IReadOnlyList<PointF> stroke, float tolerance)
        {
            var curve = WirePolyline(gripA, gripB, HitSampleCount);

            if (stroke.Count == 1)
            {
                var point = stroke[0];
                for (int i = 0; i < curve.Length - 1; i++)
                {
                    if (DistancePointSegment(point, curve[i], curve[i + 1]) <= tolerance)
                        return true;
                }
                return false;
            }

            for (int s = 0; s + 1 < stroke.Count; s++)
            {
                var a = stroke[s];
                var b = stroke[s + 1];
                for (int i = 0; i < curve.Length - 1; i++)
                {
                    var c = curve[i];
                    var d = curve[i + 1];
                    if (SegmentsIntersect(a, b, c, d)
                        || DistancePointSegment(c, a, b) <= tolerance
                        || DistancePointSegment(d, a, b) <= tolerance
                        || DistancePointSegment(a, c, d) <= tolerance
                        || DistancePointSegment(b, c, d) <= tolerance)
                        return true;
                }
            }
            return false;
        }

        private static float Cross(PointF o, PointF a, PointF b)
        {
            return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        }

        private static bool SegmentsIntersect(PointF a, PointF b, PointF c, PointF d)
        {
            float d1 = Cross(c, d, a);
            float d2 = Cross(c, d, b);
            float d3 = Cross(a, b, c);
            float d4 = Cross(a, b, d);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
                && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static float DistancePointSegment(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float lengthSq = dx * dx + dy * dy;
            if (lengthSq < 0.0001f)
                return Distance(p, a);

            float t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSq;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return Distance(p, new PointF(a.X + t * dx, a.Y + t * dy));
        }

        public static float Distance(PointF a, PointF b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static RectangleF BoundsOf(IReadOnlyList<PointF> points)
        {
            float minX = points[0].X, minY = points[0].Y;
            float maxX = minX, maxY = minY;
            for (int i = 1; i < points.Count; i++)
            {
                var p = points[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return RectangleF.FromLTRB(minX, minY, maxX, maxY);
        }
    }
}
