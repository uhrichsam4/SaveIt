import SwiftUI

/// The single black blob. Top edge is flush with the screen top; concave "ears" flare out at the
/// top corners and the bottom corners are rounded. Every dimension is animatable so the shape
/// morphs continuously notch → expanded → live activity.
struct IslandShape: Shape {
    var width: CGFloat
    var height: CGFloat
    var bottomRadius: CGFloat
    var earRadius: CGFloat
    /// When false, the top edge is left open (used for the hairline stroke).
    var closed = true

    init(_ m: IslandMetrics, closed: Bool = true) {
        width = m.width; height = m.height
        bottomRadius = m.bottomRadius; earRadius = m.earRadius
        self.closed = closed
    }

    var animatableData: AnimatablePair<AnimatablePair<CGFloat, CGFloat>, AnimatablePair<CGFloat, CGFloat>> {
        get { AnimatablePair(AnimatablePair(width, height), AnimatablePair(bottomRadius, earRadius)) }
        set {
            width = newValue.first.first; height = newValue.first.second
            bottomRadius = newValue.second.first; earRadius = newValue.second.second
        }
    }

    func path(in rect: CGRect) -> Path {
        let w = max(0, width), h = max(0, height)
        let ear = max(0, min(earRadius, h * 0.45))
        let br = max(0, min(bottomRadius, w / 2, h - ear))
        let L = rect.midX - w / 2, R = rect.midX + w / 2
        let T = rect.minY, B = rect.minY + h

        var p = Path()
        p.move(to: CGPoint(x: L - ear, y: T))
        if ear > 0.01 {
            p.addArc(tangent1End: CGPoint(x: L, y: T), tangent2End: CGPoint(x: L, y: B), radius: ear)
        } else {
            p.addLine(to: CGPoint(x: L, y: T))
        }
        if br > 0.01 {
            p.addArc(tangent1End: CGPoint(x: L, y: B), tangent2End: CGPoint(x: R, y: B), radius: br)
            p.addArc(tangent1End: CGPoint(x: R, y: B), tangent2End: CGPoint(x: R, y: T), radius: br)
        } else {
            p.addLine(to: CGPoint(x: L, y: B)); p.addLine(to: CGPoint(x: R, y: B))
        }
        if ear > 0.01 {
            p.addArc(tangent1End: CGPoint(x: R, y: T), tangent2End: CGPoint(x: R + ear, y: T), radius: ear)
        }
        p.addLine(to: CGPoint(x: R + ear, y: T))
        if closed { p.closeSubpath() }
        return p
    }
}
