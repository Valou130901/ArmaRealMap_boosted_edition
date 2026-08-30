"""Offline checks on an exported BeamNG level zip.

Every check here exists because a real defect shipped past it. None of them needs the game:
they read the zip and the terrain binary directly, so a regression is caught in seconds
instead of being found while driving.

    python tools/check-beamng-export.py <level.zip> [--dll <path>] [--absent NAME ...]
"""

import argparse
import collections
import glob
import json
import math
import os
import re
import struct
import sys
import time
import zipfile

# A step larger than this over one sample stride is a cliff, not relief.
MAX_TERRAIN_STEP_M = 25.0
TERRAIN_STRIDE = 8
# Share of triangles allowed to fall back to the untextured material.
MAX_UNTEXTURED_SHARE = 0.05
TRIANGLE_SAMPLE = 1200
# A step only matters this close to a carriageway; beyond it, steep terrain is just terrain.
CORRIDOR_REACH_M = 40.0
# Triangles smaller than this are degenerate slivers; their winding carries no information.
MIN_TRIANGLE_AREA_M2 = 1e-4
# Vertex normals are smoothed across neighbouring faces, so a sliver can legitimately
# oppose its own winding. Only a large share means the transform itself is wrong.
MAX_INVERTED_SHARE = 0.05
# A bump is a spike, not a slope: flagged when a node's grade beats the median of its
# neighbours by this factor and clears the floor below. Malden has genuine mountain roads
# descending at a sustained 25-36%, and an absolute threshold cannot tell those from a step.
ROAD_SPIKE_FACTOR = 3.0
ROAD_SPIKE_FLOOR = 0.20
ROAD_NEIGHBOURS = 4


class Report:
    def __init__(self):
        self.failed = 0

    def __call__(self, ok, name, detail):
        print(f"  [{'OK ' if ok else 'FAIL'}] {name}: {detail}")
        if not ok:
            self.failed += 1


def level_of(z):
    for name in z.namelist():
        if name.endswith(".terrain.json"):
            return name.split("/")[1]
    raise SystemExit("no terrain.json: not a BeamNG level zip")


def ndjson(z, predicate):
    for name in z.namelist():
        if name.endswith(".level.json") and predicate(name):
            for line in z.read(name).decode("utf-8", "ignore").splitlines():
                line = line.strip()
                if line:
                    yield json.loads(line)


def check_freshness(report, path, dll):
    """1. The zip must be newer than the binary that produced it."""
    if not dll or not os.path.exists(dll):
        print("  [ -- ] freshness: no dll given, skipped")
        return
    zip_at, dll_at = os.path.getmtime(path), os.path.getmtime(dll)
    fmt = lambda t: time.strftime("%H:%M:%S", time.localtime(t))
    report(zip_at > dll_at, "freshness",
           f"zip {fmt(zip_at)} vs dll {fmt(dll_at)}"
           + ("" if zip_at > dll_at else "  <- export predates the build, it runs old code"))


def check_normals(report, z, lvl):
    """3. A stored normal must agree with the winding of its own triangle."""
    daes = [n for n in z.namelist() if "/shapes/arma/" in n and n.endswith(".dae")]
    agree = oppose = 0
    for name in daes[:40]:
        s = z.read(name).decode("utf-8", "ignore")
        geom = re.search(r'<geometry id="([^"]+)-mesh"(.*?)</geometry>', s, re.S)
        if not geom:
            continue
        base, body = geom.group(1), geom.group(2)
        pos = re.search(r'id="%s-pos-array"[^>]*>([^<]+)<' % re.escape(base), body)
        nrm = re.search(r'id="%s-nrm-array"[^>]*>([^<]+)<' % re.escape(base), body)
        tri = re.search(r"<triangles[^>]*>(.*?)</triangles>", body, re.S)
        if not (pos and nrm and tri):
            continue
        p = [float(v) for v in pos.group(1).split()]
        n = [float(v) for v in nrm.group(1).split()]
        idx = [int(v) for v in re.search(r"<p>([^<]+)</p>", tri.group(1)).group(1).split()]
        stride = 3 if "TEXCOORD" in tri.group(1) else 2
        for t in range(0, min(len(idx) // stride, 300) // 3 * 3, 3):
            i = [idx[(t + k) * stride] for k in range(3)]
            v = [p[j * 3:j * 3 + 3] for j in i]
            a = [v[1][k] - v[0][k] for k in range(3)]
            b = [v[2][k] - v[0][k] for k in range(3)]
            g = [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
            ns = [sum(n[j * 3 + k] for j in i) for k in range(3)]
            # Slivers are skipped: below a square millimetre the cross product is numerical
            # noise, not an orientation. Malden's fifteen "inverted" triangles had a median
            # area of 0.000078 m2 and nothing to do with lighting.
            if 0.5 * math.sqrt(sum(c * c for c in g)) < MIN_TRIANGLE_AREA_M2:
                continue
            dot = sum(g[k] * ns[k] for k in range(3))
            if dot > 0:
                agree += 1
            elif dot < 0:
                oppose += 1
    total = agree + oppose
    if total == 0:
        print("  [ -- ] normals: no shapes to sample, skipped")
        return
    share = oppose / total
    report(share <= MAX_INVERTED_SHARE, "normals",
           f"{oppose}/{total} inverted ({share:.1%})"
           + ("" if share <= MAX_INVERTED_SHARE
              else "  <- surfaces render unlit whatever the material"))


def check_untextured(report, z):
    """4. Geometry falling back to the flat untextured material means lost rvmat textures."""
    counts = collections.Counter()
    for name in [n for n in z.namelist() if "/shapes/arma/" in n and n.endswith(".dae")][:400]:
        s = z.read(name).decode("utf-8", "ignore")
        geom = re.search(r'<geometry id="[^"]+-mesh"(.*?)</geometry>', s, re.S)
        if geom:
            for mat, cnt in re.findall(r'<triangles material="([^"]+)" count="(\d+)"', geom.group(1)):
                counts[mat] += int(cnt)
    total = sum(counts.values())
    if total == 0:
        print("  [ -- ] untextured: no shapes, skipped")
        return
    share = counts.get("grm_untextured", 0) / total
    report(share <= MAX_UNTEXTURED_SHARE, "untextured",
           f"{share:.1%} of {total} triangles on the fallback material")


def check_road_profile(report, z, lvl, roads):
    """5. The ground under a carriageway must not step.

    Scanning the whole heightmap for cliffs was the wrong question: a coastal road runs along
    a genuine 90 m drop and that is not a defect. Neither is an absolute grade: road_82 on
    Malden descends at a sustained 15-36% for nine nodes and is simply a mountain road. What
    marks a defect is a node far steeper than its own neighbours -- road_133 climbed at 43%
    between nodes doing 2% to 7%, because it met road_134 at a height 4.1 m apart.
    """
    data = z.read(f"levels/{lvl}/theTerrain.ter")
    size = struct.unpack_from("<I", data, 1)[0]
    heights = struct.unpack_from(f"<{size * size}H", data, 5)
    square, origin, max_height = 1.0, (0.0, 0.0), 666.0
    for o in ndjson(z, lambda n: True):
        if o.get("class") == "TerrainBlock":
            square = float(o.get("squareSize", 1.0))
            origin = (float(o["position"][0]), float(o["position"][1]))
            max_height = float(o.get("maxHeight", max_height))
            break
    scale = max_height / 65535.0

    def ground(wx, wy):
        gx = min(max(int((wx - origin[0]) / square), 0), size - 1)
        gy = min(max(int((wy - origin[1]) / square), 0), size - 1)
        return heights[gy * size + gx] * scale

    worst, where, context = 0.0, None, 0.0
    for nodes in roads:
        grades = []
        for i in range(1, len(nodes)):
            run = math.hypot(nodes[i][0] - nodes[i - 1][0], nodes[i][1] - nodes[i - 1][1])
            if run < 1.0:
                grades.append(None)
                continue
            grades.append(abs(ground(nodes[i][0], nodes[i][1])
                              - ground(nodes[i - 1][0], nodes[i - 1][1])) / run)
        for i, grade in enumerate(grades):
            if grade is None or grade < ROAD_SPIKE_FLOOR:
                continue
            lo, hi = max(0, i - ROAD_NEIGHBOURS), min(len(grades), i + ROAD_NEIGHBOURS + 1)
            around = sorted(g for j, g in enumerate(grades[lo:hi], lo) if g is not None and j != i)
            if not around:
                continue
            median = around[len(around) // 2]
            if grade > max(ROAD_SPIKE_FLOOR, median * ROAD_SPIKE_FACTOR) and grade > worst:
                worst, context = grade, median
                where = (round(nodes[i + 1][0]), round(nodes[i + 1][1]))
    report(where is None, "road profile",
           "no step stands out from its own road"
           if where is None
           else f"{worst:.0%} against {context:.0%} around it, at world {where}")


def check_textures_present(report, z):
    """6. Every texture a material names must exist, or the material fails to build."""
    names = {n.lower() for n in z.namelist()}
    missing = []
    for entry in [n for n in z.namelist() if n.endswith("main.materials.json")]:
        for key, value in json.loads(z.read(entry)).items():
            if not isinstance(value, dict):
                continue
            for stage in value.get("Stages") or []:
                for field, ref in (stage or {}).items():
                    if isinstance(ref, str) and ref.lower().endswith((".dds", ".png", ".jpg")):
                        if ref.lstrip("/").lower() not in names:
                            missing.append(f"{key}.{field} -> {ref}")
    report(not missing, "texture refs",
           "all resolve" if not missing else f"{len(missing)} missing, e.g. {missing[0]}")


def check_absent(report, z, keywords):
    """7. Objects that were removed must actually be gone from the placements."""
    if not keywords:
        return
    counts = collections.Counter()
    for o in ndjson(z, lambda n: "/Buildings/" in n or "/Furniture/" in n):
        shape = str(o.get("shapeName", "")).lower()
        for k in keywords:
            if k.lower() in shape:
                counts[k] += 1
    for k in keywords:
        report(counts[k] == 0, f"absent '{k}'",
               "none placed" if counts[k] == 0 else f"{counts[k]} still placed")


def road_polylines(z):
    return [o["nodes"] for o in ndjson(z, lambda n: "Decal_Roads" in n)
            if str(o.get("material", "")).startswith("grm_road")]


def road_nodes(z):
    nodes = []
    for o in ndjson(z, lambda n: "Decal_Roads" in n):
        if str(o.get("material", "")).startswith("grm_road"):
            nodes.extend((d[0], d[1], d[2]) for d in o["nodes"])
    return nodes


def check_bridges(report, z, lvl, roads):
    """8. A bridge deck must meet the road it carries.

    Not the nearest road of any direction, which is what the first version of this check
    compared against, and it was wrong: bridge_highway_f is an overpass, so the road closest
    to it is the one passing underneath and the deck is supposed to stand metres above it.
    Enforcing a zero gap there is what flattened three overpasses onto the tarmac with their
    ramps buried in the field. The road that matters is the one running along the deck.
    """
    bridges = [o for o in ndjson(z, lambda n: "/Buildings/" in n)
               if "bridge" in str(o.get("shapeName", "")).lower() and o.get("rotationMatrix")]
    segments = []
    for r in roads:
        for a, b in zip(r, r[1:]):
            segments.append(((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2,
                             math.degrees(math.atan2(b[1] - a[1], b[0] - a[0]))))
    if not bridges or not segments:
        print("  [ -- ] bridges: none to compare, skipped")
        return

    # The driving surface, not the origin. An ODOL model is autocentred, so the deck of
    # bridge_highway_f sits 3.2 m above the point the object is placed at; comparing origins
    # said the decks were 1.1 m low while the decks themselves stood 3 m above their road.
    # Estimated the same way the exporter does it, from the collision mesh.
    def deck_offset(shape_name):
        entry = "levels/%s/art/shapes/arma/%s" % (lvl, shape_name)
        try:
            dae = z.read(entry).decode("utf-8", "ignore")
        except KeyError:
            return None
        m = re.search(r'Colmesh[^"]*-pos-array"\s+count="\d+">([^<]*)<', dae)
        if not m:
            return None
        zs = sorted(float(v) for v in m.group(1).split()[2::3])
        return zs[len(zs) // 2] if zs else None

    offsets, gaps, unpaired = {}, [], 0
    for b in bridges:
        x, y, zz = b["position"]
        m = b["rotationMatrix"]
        shape = str(b.get("shapeName", "")).split("/")[-1]
        if shape not in offsets:
            offsets[shape] = deck_offset(shape)
        if offsets[shape] is None:
            unpaired += 1
            continue
        # Along the model's own Y: bridge_highway_f is 20.8 m across and 44.3 m along, and
        # looking down its X made five of eight bridges report carrying no road at all.
        yaw = math.degrees(math.atan2(m[4], m[3]))
        aligned = [s for s in segments
                   if min((yaw - s[3]) % 180, (s[3] - yaw) % 180) <= 25
                   and math.hypot(s[0] - x, s[1] - y) < 50]
        if not aligned:
            unpaired += 1
            continue
        best = min(aligned, key=lambda s: (s[0] - x) ** 2 + (s[1] - y) ** 2)
        scale = (b.get("scale") or [1, 1, 1])[2] if isinstance(b.get("scale"), list) else 1
        gaps.append(zz + offsets[shape] * scale - best[2])
    if not gaps:
        print(f"  [ -- ] bridges: {len(bridges)} bridges, none carries a road, skipped")
        return
    gaps.sort()
    median = gaps[len(gaps) // 2]
    worst = max(gaps, key=abs)
    report(abs(worst) <= 1.0, "bridge height",
           f"deck-to-carried-road median {median:+.1f} m, worst {worst:+.1f} m over {len(gaps)} bridges"
           + (f", {unpaired} carry no road" if unpaired else ""))


def check_junction_gaps(report, z):
    """12. The ground inside a junction must be covered by tarmac.

    The first version of this check measured the distance between road end nodes, and that was
    the wrong question: stitching the ends deliberately pushes each one past the centre, so the
    nodes end up further apart while the ribbons overlap better than before. The number got
    worse while the map got better. What matters is whether any bare ground is left showing
    between the ribbons, so that is what is sampled here.
    """
    roads = [o for o in ndjson(z, lambda n: "Decal_Roads" in n)
             if str(o.get("material", "")).startswith("grm_road")]
    if len(roads) < 2:
        print("  [ -- ] junction gaps: not enough roads, skipped")
        return

    segments = []
    for r in roads:
        for a, b in zip(r["nodes"], r["nodes"][1:]):
            segments.append((a[0], a[1], b[0], b[1], (a[3] + b[3]) / 4.0))

    ends = []
    for r in roads:
        ends.append((r["nodes"][0][0], r["nodes"][0][1], r["nodes"][0][3]))
        ends.append((r["nodes"][-1][0], r["nodes"][-1][1], r["nodes"][-1][3]))

    taken = [False] * len(ends)
    clusters = []
    for i, e in enumerate(ends):
        if taken[i]:
            continue
        group, taken[i] = [i], True
        for j in range(i + 1, len(ends)):
            if not taken[j] and math.hypot(ends[j][0] - e[0], ends[j][1] - e[1]) <= 24.0:
                group.append(j)
                taken[j] = True
        if len(group) > 1:
            clusters.append(group)
    if not clusters:
        print("  [ -- ] junction gaps: no junction found, skipped")
        return

    def covered(x, y):
        for ax, ay, bx, by, half in segments:
            dx, dy = bx - ax, by - ay
            length = dx * dx + dy * dy
            t = 0.0 if length == 0 else max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / length))
            px, py = ax + t * dx, ay + t * dy
            if (px - x) ** 2 + (py - y) ** 2 <= half * half:
                return True
        return False

    holes = []
    for group in clusters:
        cx = sum(ends[i][0] for i in group) / len(group)
        cy = sum(ends[i][1] for i in group) / len(group)
        radius = max(ends[i][2] for i in group) / 2.0
        total = hit = 0
        for ring in range(1, 7):
            r = radius * ring / 6
            for k in range(16):
                angle = 2 * math.pi * k / 16
                total += 1
                hit += covered(cx + r * math.cos(angle), cy + r * math.sin(angle))
        holes.append((1 - hit / total, round(cx), round(cy)))

    holes.sort(reverse=True)
    worst, wx, wy = holes[0]
    median = sorted(h[0] for h in holes)[len(holes) // 2]
    report(worst <= 0.10, "junction gaps",
           f"{len(holes)} junctions, median {median:.0%} bare, worst {worst:.0%} at world ({wx}, {wy})"
           + ("" if worst <= 0.10 else "  <- terrain shows through between the ribbons"))


def check_forest_altitude(report, z, lvl):
    """16. Forest items must carry Arma's altitude, not be snapped onto the terrain.

    An ODOL model is autocentred: its origin sits halfway up the mesh, not at its base. Stand
    that origin on the ground and the lower half of the plant is underground. Measured on
    Malden, 210 655 forest items sat a median 0.01 m above the terrain -- exactly snapped, and
    every bush half buried. Arma's own altitude already accounts for the anchor, so once it is
    carried through the offsets spread out instead of collapsing onto zero.
    """
    data = z.read(f"levels/{lvl}/theTerrain.ter")
    size = struct.unpack_from("<I", data, 1)[0]
    heights = struct.unpack_from(f"<{size * size}H", data, 5)
    square, origin, max_height = 1.0, (0.0, 0.0), 666.0
    for o in ndjson(z, lambda n: True):
        if o.get("class") == "TerrainBlock":
            square = float(o.get("squareSize", 1.0))
            origin = (float(o["position"][0]), float(o["position"][1]))
            max_height = float(o.get("maxHeight", max_height))
            break
    scale = max_height / 65535.0

    def ground(wx, wy):
        # Bilinear, because that is how the exporter sampled it. Nearest-cell sampling over
        # 3.125 m cells adds half a metre of slope noise, which is enough to make a set of
        # items pinned flat to the terrain look like a healthy spread.
        fx = (wx - origin[0]) / square
        fy = (wy - origin[1]) / square
        x0 = min(max(int(fx), 0), size - 2)
        y0 = min(max(int(fy), 0), size - 2)
        tx, ty = fx - x0, fy - y0
        h00 = heights[y0 * size + x0]
        h10 = heights[y0 * size + x0 + 1]
        h01 = heights[(y0 + 1) * size + x0]
        h11 = heights[(y0 + 1) * size + x0 + 1]
        return ((h00 * (1 - tx) + h10 * tx) * (1 - ty) + (h01 * (1 - tx) + h11 * tx) * ty) * scale

    # Per species, not overall. Rocks already carried Arma's altitude while every tree and bush
    # was snapped, and lumping them together hid it: the whole-forest spread read a healthy
    # 0.63 m while the bushes were all pinned to 0.01 m.
    per_type = collections.defaultdict(list)
    for name in z.namelist():
        if not name.endswith(".forest4.json"):
            continue
        for line in z.read(name).decode("utf-8", "ignore").splitlines():
            line = line.strip()
            if not line:
                continue
            item = json.loads(line)
            x, y, zz = item["pos"]
            per_type[item.get("type", "?")].append(zz - ground(x, y))
    if not per_type:
        # An empty forest folder is a defect, not an absence: the writer reports its tree count
        # from the data it holds, so a map that counted trees and shipped none looks silent.
        declared = 0
        for name in z.namelist():
            if name.endswith("export_report.txt"):
                for line in z.read(name).decode("utf-8", "ignore").splitlines():
                    if line.startswith("Forest:"):
                        digits = "".join(c for c in line.split()[1] if c.isdigit())
                        declared = int(digits or 0)
        if declared > 0:
            report(False, "forest altitude",
                   f"the report counts {declared} trees but the forest folder is empty"
                   "  <- the instances were computed and never written")
        else:
            print("  [ -- ] forest altitude: no forest items, skipped")
        return

    snapped, total = [], 0
    for kind, offsets in per_type.items():
        total += len(offsets)
        if len(offsets) < 500:
            continue
        offsets.sort()
        # Snapping shows as a distribution with no spread: everything on the same value.
        spread = offsets[int(len(offsets) * 0.9)] - offsets[len(offsets) // 10]
        if spread < 0.05:
            snapped.append((len(offsets), kind))
    snapped.sort(reverse=True)
    report(not snapped, "forest altitude",
           f"{total} items across {len(per_type)} species, none snapped to the ground" if not snapped
           else f"{sum(n for n, _ in snapped)} of {total} items snapped flat, worst {snapped[0][1]}"
                "  <- autocentred models are buried to their middle")


def check_junction_bumps(report, z, lvl):
    """17. The ground across a junction must not stand above its own approaches.

    The corridor pass levels each road to its own profile, cell by cell, given to whichever
    centre line is nearest. Two roads meeting at an angle leave a wedge between their corridors
    that belongs to neither and keeps the raw hillside, standing up as a mound exactly where
    the wheels cross. Measured on Malden before the junction pads went in: up to 2.72 m above
    the mean of a junction's own approaches, ninetieth percentile 0.95 m.
    """
    data = z.read(f"levels/{lvl}/theTerrain.ter")
    size = struct.unpack_from("<I", data, 1)[0]
    heights = struct.unpack_from(f"<{size * size}H", data, 5)
    square, origin, max_height = 1.0, (0.0, 0.0), 666.0
    for o in ndjson(z, lambda n: True):
        if o.get("class") == "TerrainBlock":
            square = float(o.get("squareSize", 1.0))
            origin = (float(o["position"][0]), float(o["position"][1]))
            max_height = float(o.get("maxHeight", max_height))
            break
    scale = max_height / 65535.0

    def ground(wx, wy):
        fx, fy = (wx - origin[0]) / square, (wy - origin[1]) / square
        x0 = min(max(int(fx), 0), size - 2)
        y0 = min(max(int(fy), 0), size - 2)
        tx, ty = fx - x0, fy - y0
        h00, h10 = heights[y0 * size + x0], heights[y0 * size + x0 + 1]
        h01, h11 = heights[(y0 + 1) * size + x0], heights[(y0 + 1) * size + x0 + 1]
        return ((h00 * (1 - tx) + h10 * tx) * (1 - ty) + (h01 * (1 - tx) + h11 * tx) * ty) * scale

    roads = [o["nodes"] for o in ndjson(z, lambda n: "Decal_Roads" in n)
             if str(o.get("material", "")).startswith("grm_road")]
    ends = []
    for i, nodes in enumerate(roads):
        ends.append((nodes[0][0], nodes[0][1], i, 0))
        ends.append((nodes[-1][0], nodes[-1][1], i, len(nodes) - 1))
    taken, clusters = [False] * len(ends), []
    for i, e in enumerate(ends):
        if taken[i]:
            continue
        group, taken[i] = [i], True
        for j in range(i + 1, len(ends)):
            if not taken[j] and math.hypot(ends[j][0] - e[0], ends[j][1] - e[1]) <= 24.0:
                group.append(j)
                taken[j] = True
        if len(group) > 1:
            clusters.append(group)
    if not clusters:
        print("  [ -- ] junction bumps: no junction found, skipped")
        return

    domes = []
    for group in clusters:
        cx = sum(ends[i][0] for i in group) / len(group)
        cy = sum(ends[i][1] for i in group) / len(group)
        approaches = []
        for i in group:
            _, _, road, at = ends[i]
            nodes = roads[road]
            step = 1 if at == 0 else -1
            walked, px, py, k = 0.0, nodes[at][0], nodes[at][1], at
            while 0 <= k + step < len(nodes) and walked < 25:
                nx, ny = nodes[k + step][0], nodes[k + step][1]
                walked += math.hypot(nx - px, ny - py)
                px, py, k = nx, ny, k + step
            approaches.append(ground(px, py))
        if len(approaches) >= 2:
            domes.append((ground(cx, cy) - sum(approaches) / len(approaches), round(cx), round(cy)))

    domes.sort(reverse=True)
    worst, wx, wy = domes[0]
    p90 = sorted(d[0] for d in domes)[int(len(domes) * 0.9)]
    report(worst <= 1.0, "junction bumps",
           f"{len(domes)} junctions, p90 {p90:+.2f} m, worst {worst:+.2f} m at world ({wx}, {wy})"
           + ("" if worst <= 1.0 else "  <- a mound stands where the roads cross"))


def check_road_shape(report, z):
    """15. Stitching a junction must not fold a road over itself.

    road_50 on Malden runs 9.7 m from one node to the next, so both its ends fell inside the
    24 m a junction reaches over: it was declared a junction with itself and each end dragged
    towards the other, folding the road in half. Any road shorter than the reach went the same
    way, which is why junctions still looked wrong after the holes and the tearing were gone.
    """
    roads = [o for o in ndjson(z, lambda n: "Decal_Roads" in n)
             if str(o.get("material", "")).startswith("grm_road")]
    if not roads:
        print("  [ -- ] road shape: no roads, skipped")
        return
    stubs, folds, hairpins = [], [], []
    for r in roads:
        nodes = r["nodes"]
        for a, b in zip(nodes, nodes[1:]):
            if math.hypot(b[0] - a[0], b[1] - a[1]) < 1.0:
                stubs.append(r.get("name"))
                break
        last = len(nodes) - 1
        for i, (a, b, c) in enumerate(zip(nodes, nodes[1:], nodes[2:])):
            ux, uy = b[0] - a[0], b[1] - a[1]
            vx, vy = c[0] - b[0], c[1] - b[1]
            nu, nv = math.hypot(ux, uy), math.hypot(vx, vy)
            if nu < 0.01 or nv < 0.01:
                continue
            cos = (ux * vx + uy * vy) / (nu * nv)
            if cos >= -0.87:  # under 150 degrees is a bend, not a fold
                continue
            # Only the two end nodes can be the export's doing. A hairpin in the middle of a
            # mountain road is Arma's own geometry and stitching never touches it.
            (folds if i + 1 == 1 or i + 1 == last - 1 else hairpins).append(r.get("name"))
            break
    bad = sorted(set(stubs) | set(folds))
    note = f", {len(set(hairpins))} hairpins mid-road left as Arma drew them" if hairpins else ""
    report(not bad, "road shape",
           f"{len(roads)} carriageways, no end folded or collapsed{note}" if not bad
           else f"{len(bad)} folded or collapsed at an end, e.g. {bad[0]}{note}"
                "  <- stitching dragged a tip over the road's own next segment")


def check_decal_priority(report, z):
    """13. Two carriageways that meet must not share a renderPriority.

    Decals with the same priority have no defined draw order, so where two ribbons overlap at a
    junction the pair tears into a mosaic of triangles taken from both. The game's own levels
    spread a single asphalt material across priorities 3 to 50 for exactly this reason.
    """
    roads = [o for o in ndjson(z, lambda n: "Decal_Roads" in n)
             if str(o.get("material", "")).startswith("grm_road")]
    if len(roads) < 2:
        print("  [ -- ] decal priority: not enough roads, skipped")
        return
    # Whole ribbons, not just their ends: two roads crossing in open country overlap just as
    # surely as two that meet at a junction, and an endpoint test cannot see it. That blind
    # spot let 164 of Malden's 216 carriageways sit on the same priority and tear.
    def box(r):
        xs = [n[0] for n in r["nodes"]]
        ys = [n[1] for n in r["nodes"]]
        half = max(n[3] for n in r["nodes"]) / 2
        return min(xs) - half, min(ys) - half, max(xs) + half, max(ys) + half

    boxes = [box(r) for r in roads]
    clashes = 0
    pairs = 0
    for i, a in enumerate(roads):
        ax0, ay0, ax1, ay1 = boxes[i]
        for j in range(i + 1, len(roads)):
            bx0, by0, bx1, by1 = boxes[j]
            if ax0 > bx1 or bx0 > ax1 or ay0 > by1 or by0 > ay1:
                continue
            pairs += 1
            if a.get("renderPriority") == roads[j].get("renderPriority"):
                clashes += 1
    report(clashes == 0, "decal priority",
           f"{pairs} overlapping road pairs, {clashes} share a priority"
           + ("" if clashes == 0 else "  <- the two ribbons tear into each other"))


def check_bridge_gap(report, z, lvl):
    """14. There must be a gap under a bridge.

    A deck only reads as a bridge if the ground drops away beneath it. The corridor pass levels
    the ground along the carriageway, and over a bridge that fills in the ravine the bridge
    spans: measured on Malden, the terrain across all eight decks was flat to within a metre and
    every one sat in the dirt with its ramps buried.
    """
    data = z.read(f"levels/{lvl}/theTerrain.ter")
    size = struct.unpack_from("<I", data, 1)[0]
    heights = struct.unpack_from(f"<{size * size}H", data, 5)
    square, origin, max_height = 1.0, (0.0, 0.0), 666.0
    for o in ndjson(z, lambda n: True):
        if o.get("class") == "TerrainBlock":
            square = float(o.get("squareSize", 1.0))
            origin = (float(o["position"][0]), float(o["position"][1]))
            max_height = float(o.get("maxHeight", max_height))
            break
    scale = max_height / 65535.0

    def ground(wx, wy):
        gx = min(max(int((wx - origin[0]) / square), 0), size - 1)
        gy = min(max(int((wy - origin[1]) / square), 0), size - 1)
        return heights[gy * size + gx] * scale

    bridges = [o for o in ndjson(z, lambda n: "/Buildings/" in n)
               if "bridge" in str(o.get("shapeName", "")).lower() and o.get("rotationMatrix")]
    if not bridges:
        print("  [ -- ] bridge gap: no bridges, skipped")
        return

    dips = []
    for b in bridges:
        x, y, _ = b["position"]
        m = b["rotationMatrix"]
        ax, ay = m[0], m[1]
        # The abutments sit along the deck axis; the gap is between them
        ends = [ground(x + ax * d, y + ay * d) for d in (-25, 25)]
        dips.append(max(ends) - ground(x, y))
    dips.sort()
    median = dips[len(dips) // 2]
    report(median >= 1.0, "bridge gap",
           f"median ground drop under {len(dips)} decks {median:+.1f} m"
           + ("" if median >= 1.0 else "  <- the ravine was filled in, decks sit in the dirt"))


def check_road_specular(report, z, lvl):
    """11. A road material must not be shiny, or daylight blows it out to pure white.

    The roads shipped as a mirror: colorMap on a dark grey asphalt, specularPower 1 and no
    specular strength at all, so the sun turned every carriageway into a white sheet with
    only the grain of its own noise showing through. specularPower 1 is fine and is the most
    common value in the game's own materials -- all 192 vanilla materials that use it pair it
    with a specular strength of zero.
    """
    try:
        mats = json.loads(z.read(f"levels/{lvl}/art/roads/main.materials.json"))
    except KeyError:
        print("  [ -- ] road specular: no road materials, skipped")
        return
    shiny = []
    for name, mat in mats.items():
        if not isinstance(mat, dict):
            continue
        stage = (mat.get("Stages") or [{}])[0] or {}
        if mat.get("version") == 1.5:
            continue  # PBR: roughness carries it, not a specular strength
        if str(mat.get("specularStrength0", "")) not in ("0", "0.0"):
            shiny.append(f"{name} (specularPower {stage.get('specularPower')})")
    report(not shiny, "road specular",
           f"{len(mats)} road materials, none shiny" if not shiny
           else f"{len(shiny)} with specular left on: {shiny[0]}  <- renders white in daylight")


def check_night_lights(report, z):
    """9. Lamp posts must carry a light that the engine turns on at dusk.

    Three things have to hold together or the map stays black at night: the light must be
    flagged nightLight (the flag the engine flips), it must ship disabled so it does not
    burn during the day, and it must sit high enough above its own post to light the road
    rather than the inside of the housing.
    """
    # The group files in that folder also declare the SimGroups, which carry no position
    lights = [o for o in ndjson(z, lambda n: "nightlights" in n)
              if o.get("class") in ("PointLight", "SpotLight")]
    lamps = [o for o in ndjson(z, lambda n: "/Buildings/" in n)
             if "lamp" in str(o.get("shapeName", "")).lower()
             and "_off" not in str(o.get("shapeName", "")).lower()]
    if not lamps:
        print("  [ -- ] night lights: no lamp posts on this map, skipped")
        return
    if not lights:
        report(False, "night lights", f"none, though {len(lamps)} lamp posts are placed")
        return
    bad_flag = [o for o in lights
                if str(o.get("nightLight")) != "1" or o.get("isEnabled") not in (False, "false")]
    heights = []
    for light in lights:
        lx, ly, lz = light["position"]
        post = min(lamps, key=lambda o: (o["position"][0] - lx) ** 2 + (o["position"][1] - ly) ** 2)
        if math.hypot(post["position"][0] - lx, post["position"][1] - ly) < 2.0:
            heights.append(lz - post["position"][2])
    heights.sort()
    median = heights[len(heights) // 2] if heights else 0.0
    ok = not bad_flag and 2.0 <= median <= 14.0
    detail = f"{len(lights)} lights on {len(lamps)} posts, median {median:+.1f} m above the base"
    if bad_flag:
        detail += f"  <- {len(bad_flag)} not flagged nightLight/disabled"
    elif not ok:
        detail += "  <- not at lamp head height"
    report(ok, "night lights", detail)


def check_places(report, z):
    """10. Town names must reach the level, including the ones behind an #include."""
    # Every level gets one default drop point; only the named ones prove the config was read
    spawns = [o for o in ndjson(z, lambda n: True)
              if o.get("class") == "SpawnSphere" and o.get("name") != "spawn_default"]
    report(bool(spawns), "places",
           f"{len(spawns)} named spawn points, e.g. {spawns[0].get('name')}"
           if spawns else "only the default drop point: no town labels or fast travel destinations")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("zip", nargs="?")
    ap.add_argument("--dll")
    ap.add_argument("--absent", nargs="*", default=[])
    args = ap.parse_args()

    mods = glob.glob(os.path.expandvars(
        r"%LOCALAPPDATA%/BeamNG/BeamNG.drive/current/mods/*beamng.zip"))
    path = args.zip
    if path and not os.path.exists(path):
        # A bare map name is enough: "malden", "romont". Saves spelling out the mods path, and
        # saves picking the wrong map when several are exported.
        wanted = os.path.basename(path).lower().removesuffix(".zip")
        matches = [m for m in mods if wanted in os.path.basename(m).lower()]
        if not matches:
            raise SystemExit(f"no zip at {path}, and nothing matching '{wanted}' in the mods folder:\n  "
                             + "\n  ".join(os.path.basename(m) for m in mods))
        path = max(matches, key=os.path.getmtime)
    if not path:
        if not mods:
            raise SystemExit("no zip given and none found in the mods folder")
        path = max(mods, key=os.path.getmtime)

    print(f"checking {os.path.basename(path)}")
    z = zipfile.ZipFile(path)
    lvl = level_of(z)
    report = Report()

    check_freshness(report, path, args.dll)
    check_normals(report, z, lvl)
    check_untextured(report, z)
    check_road_profile(report, z, lvl, road_polylines(z))
    check_textures_present(report, z)
    check_absent(report, z, args.absent)
    check_bridges(report, z, lvl, road_polylines(z))
    check_junction_gaps(report, z)
    check_junction_bumps(report, z, lvl)
    check_road_shape(report, z)
    check_forest_altitude(report, z, lvl)
    check_decal_priority(report, z)
    check_bridge_gap(report, z, lvl)
    check_road_specular(report, z, lvl)
    check_night_lights(report, z)
    check_places(report, z)

    print(f"\n{report.failed} failed")
    return 1 if report.failed else 0


if __name__ == "__main__":
    sys.exit(main())
