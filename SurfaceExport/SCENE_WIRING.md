# SCENE_WIRING.md — Surface/Paint System: Complete Scene Wiring
**Source:** `Assets/Scenes/SampleScene.unity`  
**Exported:** 2026-07-01  
**Purpose:** Self-contained rebuild reference. Every fileID, field value, and cross-reference needed to recreate the painting system in a fresh Unity project with no shared git history.

---

## 1. TRANSFORM HIERARCHY (painting system only)

```
[root]
├── Canvas                       (fileID: 1371810639)
├── Painter                      (fileID: 1562502121)
├── PaintStream                  (fileID: 2030515568)
└── Pivot                        (fileID: 659739582)
    └── Bucket                   (fileID: 1953271162)   ← child of Pivot
        ├── inner                (fileID: 1478215010)   ← child of Bucket
        │   ├── toppoint         (fileID: 996133994)    ← child of inner
        │   └── PaintPoint       (fileID: 1421697518)   ← child of inner  ← USED BY PaintStream
        ├── PaintPoint           (fileID: 558370782)    ← direct child of Bucket  ← USED BY Painter/PaintPhysics
        └── HandlePoint          (fileID: 1567880980)   ← child of Bucket

[Other roots - not part of painting system]
├── Main Camera                  (fileID: 963194225)
├── Directional Light            (fileID: 705507993)
├── RopeAnchor                   (fileID: 954778649)
├── Rope                         (fileID: 197968574)
└── ParticleRenderer10k          (fileID: 1053770533)
```

---

## 2. GAMEOBJECTS: DETAILED BREAKDOWN

### 2.1 Canvas (the floor/paint surface)
| Property | Value |
|---|---|
| **GameObject name** | Canvas |
| **fileID** | 1371810639 |
| **Active** | true |
| **Parent** | none (root) |
| **Children** | none |
| **World position** | (0, -3, 0) |
| **Local scale** | (5, 1, 5)  → real size = 10*5 = **50 m × 50 m** (Unity Plane mesh × scale) |

**Components:**
| fileID | Type |
|---|---|
| 1371810643 | Transform |
| 1371810641 | MeshRenderer  ← **this fileID is what canvasRenderer points to in PaintPhysics** |
| 1371810642 | MeshFilter |

**Transform (1371810643):**
```
localPosition: (0, -3, 0)
localRotation: (0, 0, 0, 1)  — identity (horizontal)
localScale:    (5, 1, 5)
parent:        none (root)
children:      none
```

**MeshRenderer (1371810641):**
```
material: fileID 10303, guid 0000000000000000f000000000000000
          → Unity built-in "Default-Diffuse" material (white)
NOTE: PaintPhysics.Start() replaces mainTexture at runtime with a procedural Texture2D
      of size 1024×1024. The material slot is irrelevant to painting — only used for
      initial display. The renderer reference is how PaintPhysics knows the plane transform.
```

**MeshFilter (1371810642):**
```
mesh: fileID 10209, guid 0000000000000000e000000000000000
      → Unity built-in Plane mesh (10×10 local units)
```

---

### 2.2 Painter (holds PaintPhysics / surface interaction logic)
| Property | Value |
|---|---|
| **GameObject name** | Painter |
| **fileID** | 1562502121 |
| **Active** | true |
| **Parent** | none (root) |
| **Children** | none |
| **World position** | (0, 0, 0) |

**Components:**
| fileID | Type |
|---|---|
| 1562502123 | Transform |
| 1562502122 | MonoBehaviour (PaintPhysics, script in PaintDrawer.cs) |

**Transform (1562502123):**
```
localPosition: (0, 0, 0)
localRotation: identity
localScale:    (1, 1, 1)
parent:        none (root)
children:      none
```

**MonoBehaviour — PaintPhysics (1562502122):**  
Script GUID: `a1727060f0d1c03439c39d1fc507934b` (= PaintDrawer.cs)

> **INSPECTOR-ASSIGNED OBJECT REFERENCES:**

| Field | Target fileID | Target GameObject | Target Component Type |
|---|---|---|---|
| `paintPoint` | 558370783 | PaintPoint (direct child of Bucket) | Transform |
| `canvasRenderer` | 1371810641 | Canvas | MeshRenderer |
| `bucketMotion` | 1953271167 | Bucket | PendulumMotion (MonoBehaviour) |

> **ALL SERIALIZED VALUE FIELDS:**

```yaml
# --- Particle sleep ---
sleepVelocityThreshold: 0.05
sleepTime: 1.5

# --- Particle interaction forces ---
particleRepulsion: 2
viscosityStrength: 0.5

# --- Surface ---
surface: 0          # SurfaceType enum: 0=Canvas, 1=Wood, 2=Metal, 3=Paper
paintColor: {r:1, g:0, b:0, a:1}   # RED

# --- Paint reservoir ---
maxPaintAmount: 5
currentPaintAmount: 5

# --- Viscosity / temperature / humidity ---
viscosity: 1
temperature: 25
minViscosity: 0.3
maxViscosity: 3
humidity: 50        # [0..100]

# --- Hole ---
holeShape: 0        # HoleShape enum: 0=Round
holeRadius: 0.06
holeHeight: 0.1
holeFactor: 1
exitDirection: {x:0, y:-1, z:0}   # straight down

# --- Bucket geometry ---
bucketRadius: 0.1

# --- Emission ---
baseEmission: 40
maxParticles: 10000
baseSpeed: 0.2
baseSize: 0.05
baseSpread: 0.12
particleLifetime: 100
dampingFactor: 0.96

# --- Particle interaction ---
enableParticleInteraction: 1
interactionRadius: 0.25
cohesionStrength: 0.5
separationStrength: 1

# --- Drawing / surface ---
gravity: 9.81
textureSize: 1024
baseSplatSize: 8
continuousJetMode: 1
jetMaxGapUV: 0.04
crownSplashEnabled: 0

# --- Paint fluid properties ---
# NOTE: These two are STALE (water values from an old script version).
# PaintPhysics.Start() detects them and overrides at runtime:
#   density=1000 → overridden to FluidConstants.PaintDensity (1300 kg/m³)
#   surfaceTension=0.072 → overridden to FluidConstants.PaintSurfaceTension (0.035 N/m)
density: 1000
surfaceTension: 0.072

# --- Display debug ---
currentEmissionRate: 0
paintLevel: 0
isHoleSubmerged: 0
activeParticles: 0
Re: 0
We: 0
Ca: 0
Oh: 0
```

> **STALE SERIALIZED FIELDS** (present in YAML but removed from current PaintDrawer.cs):
These remain in SampleScene.unity but are ignored by the current script. Do NOT add them back.
```
particleRenderer: {fileID:0}
maxSpawnPerFrame: 50
crownWeberThreshold: 400
adhesionStrength: 0.8
absorptionRate: 0.3
surfaceGravity: 1
maxPaintThickness: 1
thicknessAdd: 0.05
```

> **FIELDS NOT SERIALIZED IN YAML** (code-default or runtime-computed — set these in Inspector or they use defaults):
```
paintViscosityPaS          = FluidConstants.PaintViscosity  (0.1 Pa·s)
substrateThicknessMeters   = 0.001  (1 mm)
maxFilmThicknessMeters     = 0.002  (2 mm)
pixelsPerUnit              computed in Start()
canvasMetersWidth          computed in Start()
canvasMetersHeight         computed in Start()
canvasTiltControlDeg       = 0f  (default)
canvasTiltRollDeg          = 0f  (default)
```

---

### 2.3 Pivot (pendulum suspension point)
| Property | Value |
|---|---|
| **GameObject name** | Pivot |
| **fileID** | 659739582 |
| **Active** | true |
| **Parent** | none (root) |
| **Children** | Bucket (fileID 1953271162) |
| **Purpose** | The fixed point the bucket swings from. PendulumMotion reads `pivot` to find this transform. |

**Transform (659739583):**
```
localPosition: (0, 3, 0)   — 3 metres above world origin
localRotation: identity
localScale:    (1, 1, 1)
parent:        none (root)
children:      [{fileID: 1953271163}]  → Bucket transform
```

---

### 2.4 Bucket (the paint container — pendulum bob)
| Property | Value |
|---|---|
| **GameObject name** | Bucket |
| **fileID** | 1953271162 |
| **Active** | true |
| **Parent** | Pivot (fileID 659739582) |
| **Children** | inner, PaintPoint (direct), HandlePoint |
| **World position** | (0, -5, 0) in scene (local to Pivot: (0, -5, 0)) |
| **Local scale** | (1.5, 0.6, 1.5) |

**Components:**
| fileID | Type |
|---|---|
| 1953271163 | Transform |
| 1953271166 | MeshFilter |
| 1953271165 | MeshRenderer |
| 1953271167 | MonoBehaviour (PendulumMotion) |
| 1953271168 | LineRenderer |
| 1953271169 | Rigidbody |

**Transform (1953271163):**
```
localPosition: (0, -5, 0)
localRotation: identity
localScale:    (1.5, 0.6, 1.5)
parent:        659739583  (Pivot)
children:
  - 1478215011  (inner )
  - 558370783   (PaintPoint direct child)
  - 1567880981  (HandlePoint)
```

**MeshFilter (1953271166):**
```
mesh: fileID 10206, guid 0000000000000000e000000000000000  → Unity built-in Cylinder mesh
```

**MeshRenderer (1953271165):**
```
material: fileID 2100000, guid d8fa0f03d5eaf8b4e89f3bd6239fd947
          → Bucket_Outer_Mat.mat
          → Shader: Standard (built-in)
          → _Color: {r:0.4, g:0.4, b:0.4, a:1}  (dark grey)
```

**MonoBehaviour — PendulumMotion (1953271167):**  
Script GUID: `7b84e623f11d7c84ab0d72b409737dc7`

> **INSPECTOR-ASSIGNED OBJECT REFERENCES:**

| Field | Target fileID | Target GameObject | Target Component Type |
|---|---|---|---|
| `pivot` | 659739583 | Pivot | Transform |

> **ALL SERIALIZED VALUE FIELDS:**

```yaml
L: 5                    # rope length (m)
ropeIsElastic: 0        # false = rigid pendulum
ropeStiffness: 5000
ropeBreakTension: 0
ropeBroken: 0
emptyMass: 1            # bucket mass when empty (kg)
initialPaintMass: 0.5   # paint mass at start (kg)
flowRate: 0.05          # paint drain rate (kg/s)
g: 9.81
airDensity: 1.225
dragCoef: 1
area: 0.05              # cross-section for air drag (m²)
windVel: {x:0, y:0, z:0}
useBuoyancy: 0
bucketVolume: 0.005     # internal volume (m³)
damping: 0.05           # angular damping coefficient
initialAngleDeg: 30     # starting tilt angle (degrees)
initialAngVel: 2        # initial angular velocity (rad/s)
impulse: 2
angleX: 0               # live tilt angle X (debug)
angleZ: 0               # live tilt angle Z (debug)
displayMass: 0
velocity: {x:0, y:0, z:0}    # live linear velocity (debug)
ropeIsSlack: 0
currentTension: 0
kineticEnergy: 0
potentialEnergy: 0
totalEnergy: 0
theoreticalPeriod: 0
energyDissipationRate: 0
validationMode: 0
```

**LineRenderer (1953271168):**
```
material: fileID 2100000, guid e12983fc07afa664685f5483e0a7a0a4  → RopeMat.mat
widthMultiplier: 0   (zero width = invisible, rope visual handled by RopeTubeRenderer on the Rope GO)
```

**Rigidbody (1953271169):**
```
mass: 2
drag: 0
angularDrag: 0.05
useGravity: 1
isKinematic: 0
```
> **IMPORTANT NOTE:** A Rigidbody exists on Bucket. The official assignment says "NO Rigidbody/Collider without approval." This Rigidbody was present before the surface-physics branch and is inherited. The pendulum physics is driven by PendulumMotion (custom integration), NOT by the Rigidbody. If rebuilding from scratch without the Rigidbody, PendulumMotion should still work since it directly sets transform.position.

---

### 2.5 PaintPoint (direct child of Bucket) ← USED BY Painter/PaintPhysics
| Property | Value |
|---|---|
| **GameObject name** | PaintPoint |
| **fileID** | 558370782 |
| **Active** | true |
| **Parent** | Bucket (transform fileID 1953271163) |
| **Children** | none |

**Transform (558370783):**
```
localPosition: (0, -0.7, 0)   — 0.7 m below bucket centre (the hole exit point)
localRotation: identity
localScale:    (1, 1, 1)
parent:        1953271163  (Bucket)
children:      none
```
> This is the Transform that `PaintPhysics.paintPoint` points to (fileID 558370783).  
> Particles spawn at `paintPoint.position + spawnOffset` in world space.

---

### 2.6 PaintPoint (child of 'inner') ← USED BY PaintStream
| Property | Value |
|---|---|
| **GameObject name** | PaintPoint |
| **fileID** | 1421697518 |
| **Active** | true |
| **Parent** | inner  (transform fileID 1478215011) |
| **Children** | none |

**Transform (1421697519):**
```
localPosition: (0, -0.7, 0)
localRotation: identity
localScale:    (1, 1, 1)
parent:        1478215011  (inner )
children:      none
```

---

### 2.7 inner  (inner mesh of the bucket)
| Property | Value |
|---|---|
| **GameObject name** | inner  (note: trailing space in name) |
| **fileID** | 1478215010 |
| **Active** | true |
| **Parent** | Bucket (transform 1953271163) |
| **Children** | toppoint (996133994), PaintPoint (1421697518) |

**Transform (1478215011):**
```
localPosition: (0, 0, 0)
localRotation: identity
localScale:    (0.9, 0.95, 0.9)
parent:        1953271163  (Bucket)
children:
  - 996133995  (toppoint)
  - 1421697519 (PaintPoint of inner)
```

**MeshFilter (1478215015):**
```
mesh: fileID 10206  → Unity built-in Cylinder mesh
```

**MeshRenderer (1478215014):**
```
material: fileID 2100000, guid 59aac94486648214193157ad936647b6
          → Bucket_inner_Mat.mat
          → _Color: {r:0.9, g:0.9, b:0.9, a:1}  (near-white)
```

---

### 2.8 HandlePoint
| Property | Value |
|---|---|
| **GameObject name** | HandlePoint |
| **fileID** | 1567880980 |
| **Active** | true |
| **Parent** | Bucket (transform 1953271163) |
| **Children** | none |

**Transform (1567880981):**
```
localPosition: (0, 1.5, 0)   — above bucket centre (rope attachment)
localRotation: identity
parent:        1953271163  (Bucket)
```

---

### 2.9 toppoint
| Property | Value |
|---|---|
| **GameObject name** | toppoint |
| **fileID** | 996133994 |
| **Active** | true |
| **Parent** | inner  (transform 1478215011) |
| **Children** | none |

**Transform (996133995):**
```
localPosition: (0, 0.2, 0)
parent:        1478215011  (inner )
```

---

### 2.10 PaintStream
| Property | Value |
|---|---|
| **GameObject name** | PaintStream |
| **fileID** | 2030515568 |
| **Active** | true |
| **Parent** | none (root) |
| **Children** | none |
| **World position** | (-4.28571, -3, -4.28571) |

**Components:**
| fileID | Type |
|---|---|
| 2030515570 | Transform |
| 2030515569 | LineRenderer |
| 2030515571 | MonoBehaviour (PaintStream) |

**Transform (2030515570):**
```
localPosition: (-4.28571, -3, -4.28571)
localRotation: identity
localScale:    (1, 1, 1)
parent:        none (root)
children:      none
```

**LineRenderer (2030515569):**
```
material: fileID 10306, guid 0000000000000000f000000000000000  → Unity built-in "Sprites/Default"
widthMultiplier: 1
widthCurve: single key at time=0.0097, value=0.0224  (≈ 2 cm wide stream)
colorGradient: key0={r:0.766, g:0.126, b:0.126, a:1}  → dark red; key1=white
20 positions (all zero at save time — updated at runtime by PaintStream.cs)
```

**MonoBehaviour — PaintStream (2030515571):**  
Script GUID: `e00ee287670ef7143bf4e3df46a7e0d6`

| Field | Target fileID | Target GameObject | Note |
|---|---|---|---|
| `paintPoint` | 1421697519 | PaintPoint (child of inner ) | Transform |
| `canvas` | 0 | **NOT ASSIGNED** | null — PaintStream visual only |

---

## 3. MATERIALS REFERENCED

| Material File | GUID | Shader | Color | Used By |
|---|---|---|---|---|
| Bucket_Outer_Mat.mat | d8fa0f03d5eaf8b4e89f3bd6239fd947 | Standard | (0.4, 0.4, 0.4) grey | Bucket MeshRenderer |
| Bucket_inner_Mat.mat | 59aac94486648214193157ad936647b6 | Standard | (0.9, 0.9, 0.9) near-white | inner  MeshRenderer |
| PaintCanvasMat.mat | (see Assets/) | Standard | (1,1,1) white | Not assigned in scene directly; available in project |
| RopeMat.mat | e12983fc07afa664685f5483e0a7a0a4 | Standard | default | Bucket LineRenderer + Rope |
| *built-in Default-Diffuse* | 0000000000000000f000000000000000 fileID 10303 | Standard (legacy) | white | Canvas MeshRenderer (replaced at runtime by procedural texture) |
| *built-in Sprites/Default* | 0000000000000000f000000000000000 fileID 10306 | Sprites/Default | - | PaintStream LineRenderer |

> **substrateColor (not a Material asset):** The bare surface colour is a `Color` field embedded in `SurfacePreset` (a pure-code struct). It is NOT a texture or material — it lives only in the script. At runtime the Canvas texture is initialized to `preset.substrateColor` via `texture.SetPixels()`.

---

## 4. SCENE ROOTS ORDER
The SceneRoots block lists roots in this order (transforms):
1. 963194228 — Main Camera
2. 705507995 — Directional Light
3. 1371810643 — Canvas
4. 659739583 — Pivot
5. 1562502123 — Painter
6. 2030515570 — PaintStream
7. 197968577 — Rope
8. 1053770535 — ParticleRenderer10k
9. 954778650 — RopeAnchor

---

## 5. CRITICAL REBUILD NOTES

1. **Two PaintPoints exist.** `PaintPhysics.paintPoint` must be set to the DIRECT child of Bucket (not the child of 'inner'). They both share the same local position (0, -0.7, 0) but one is parented to Bucket directly and the other is parented to 'inner' (scaled 0.9×0.95×0.9). Assigning the wrong one shifts the emission origin slightly.

2. **Canvas must be a Plane** (Unity built-in, 10×10 local units) scaled (5,1,5) so the physical size is 50×50 m. `PaintPhysics.ComputeUnitScale()` reads `lossyScale` to compute pixelsPerUnit — any other mesh or scale breaks the coordinate math.

3. **The Canvas MeshRenderer material is replaced at runtime.** At `Start()`, `PaintPhysics` does `canvasRenderer.material.mainTexture = texture` (a new Texture2D). The Inspector material is irrelevant to the physics — it just controls what you see before the simulation starts.

4. **Stale serialized values in density/surfaceTension.** The scene YAML stores `density: 1000` and `surfaceTension: 0.072` (water values). `PaintPhysics.Start()` detects and overrides them to `PaintDensity=1300` and `PaintSurfaceTension=0.035`. Do NOT manually change them — the override logic handles it.

5. **PendulumMotion.pivot must point to the Pivot Transform.** Without this, the bucket doesn't know its suspension point and the angular velocity computation breaks. `PaintPhysics.bucketMotion` then gets zero velocity → no emission.

6. **PaintStream.canvas is null** (fileID 0 in scene). PaintStream is a visual-only LineRenderer helper; it does not write to the paint texture. It reads `paintPoint.position` to draw the stream trail.

7. **Rigidbody on Bucket.** Present in the original project. PendulumMotion overrides transform directly, so the Rigidbody has no physics role beyond mass storage. If you rebuild without it, remove any `GetComponent<Rigidbody>()` references in PendulumMotion.
