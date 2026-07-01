using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  SpatialGrid — spatial hash replacing the old Dictionary<Vector3Int,List<>>.
// ----------------------------------------------------------------------------
//  Zero per-frame heap allocation: all data lives in pre-allocated int arrays;
//  the neighbor result is a single reused scratch List that the caller must
//  consume before the next GetNeighbors call (safe in single-threaded Update).
//
//  Algorithm: linked-list chaining (Müller et al., SCA 2003).
//    AddParticle  O(1)         — prepend into bucket chain.
//    GetNeighbors O(k)         — walk 27 chains, k = actual neighbors.
//    Clear        O(tableSize) — reset heads (fast int loop, no GC).
//
//  Hash collisions between different cells add false-positive particles to the
//  neighbor list. The SPH kernels evaluate to 0 for r > h, so false positives
//  are filtered with no correctness impact (they just add one Poly6 evaluation
//  that immediately returns 0).
// ============================================================================
public class SpatialGrid
{
    private readonly float cellSize;
    private readonly int   tableSize;

    // bucketHead[h]  = index of the first particle in bucket h (-1 = empty).
    // nextInChain[i] = next particle index in the same chain  (-1 = end).
    private readonly int[] bucketHead;
    private readonly int[] nextInChain;

    // Flat snapshot of particles indexed 0..buildCount-1, filled by AddParticle.
    private PaintParticle[] particleArr;
    private int buildCount;

    // Single reused scratch — avoids a new List<> allocation per GetNeighbors call.
    private readonly List<PaintParticle> scratch;

    // Distinct large primes → well-distributed buckets for typical 3-D coordinate ranges.
    private const int P1 = 73856093, P2 = 19349663, P3 = 83492791;

    public SpatialGrid(float cellSize, int maxParticles = 10000)
    {
        this.cellSize = Mathf.Max(cellSize, 1e-4f);
        // First prime > 2 × N keeps average chain length ≤ 0.5.
        tableSize   = NextPrime(maxParticles * 2 + 7);
        bucketHead  = new int[tableSize];
        nextInChain = new int[maxParticles + 1];
        particleArr = new PaintParticle[maxParticles + 1];
        scratch     = new List<PaintParticle>(256);
        ResetHeads();
    }

    // Call once per frame BEFORE adding particles.
    public void Clear()
    {
        ResetHeads();
        buildCount = 0;
    }

    private void ResetHeads()
    {
        for (int i = 0; i < tableSize; i++) bucketHead[i] = -1;
    }

    // O(1) — prepend the particle into its bucket's chain.
    public void AddParticle(PaintParticle p)
    {
        if (buildCount >= particleArr.Length - 1) return;
        particleArr[buildCount] = p;
        int h = HashPos(p.position);
        nextInChain[buildCount] = bucketHead[h];
        bucketHead[h] = buildCount;
        buildCount++;
    }

    // Returns the REUSED scratch list. Consume before the next GetNeighbors call.
    public List<PaintParticle> GetNeighbors(Vector3 pos)
    {
        scratch.Clear();
        int cx = Cell(pos.x), cy = Cell(pos.y), cz = Cell(pos.z);
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            int h   = Hash(cx + dx, cy + dy, cz + dz);
            int idx = bucketHead[h];
            while (idx >= 0)
            {
                scratch.Add(particleArr[idx]);
                idx = nextInChain[idx];
            }
        }
        return scratch;
    }

    private int Cell(float v)    => Mathf.FloorToInt(v / cellSize);
    private int HashPos(Vector3 p) => Hash(Cell(p.x), Cell(p.y), Cell(p.z));

    private int Hash(int x, int y, int z)
    {
        int h = (x * P1) ^ (y * P2) ^ (z * P3);
        return ((h % tableSize) + tableSize) % tableSize;
    }

    // Smallest prime ≥ n (used once at construction time only).
    private static int NextPrime(int n)
    {
        if (n <= 2) return 2;
        int c = (n % 2 == 0) ? n + 1 : n;
        for (; ; c += 2)
            if (IsPrime(c)) return c;
    }

    private static bool IsPrime(int n)
    {
        if (n < 2)  return false;
        if (n < 4)  return true;
        if (n % 2 == 0 || n % 3 == 0) return false;
        for (int i = 5; (long)i * i <= n; i += 6)
            if (n % i == 0 || n % (i + 2) == 0) return false;
        return true;
    }
}
