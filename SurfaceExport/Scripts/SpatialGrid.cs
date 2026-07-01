using System.Collections.Generic;
using UnityEngine;

public class SpatialGrid
{
    private float cellSize;

    private Dictionary<Vector3Int, List<PaintParticle>> cells;


    public SpatialGrid(float size)
    {
        cellSize = size;

        cells = new Dictionary<Vector3Int, List<PaintParticle>>();
    }


    private Vector3Int GetCell(Vector3 position)
    {
        return new Vector3Int(
            Mathf.FloorToInt(position.x / cellSize),
            Mathf.FloorToInt(position.y / cellSize),
            Mathf.FloorToInt(position.z / cellSize)
        );
    }


    public void Clear()
    {
        cells.Clear();
    }


    public void AddParticle(PaintParticle particle)
    {
        Vector3Int cell = GetCell(particle.position);


        if (!cells.ContainsKey(cell))
        {
            cells[cell] = new List<PaintParticle>();
        }


        cells[cell].Add(particle);
    }



    public List<PaintParticle> GetNeighbors(Vector3 position)
    {
        List<PaintParticle> result = new List<PaintParticle>();

        Vector3Int center = GetCell(position);


        // البحث في 27 خلية حول الجزيء
        for(int x=-1;x<=1;x++)
        {
            for(int y=-1;y<=1;y++)
            {
                for(int z=-1;z<=1;z++)
                {

                    Vector3Int key =
                    center + new Vector3Int(x,y,z);


                    if(cells.TryGetValue(key,out List<PaintParticle> list))
                    {

                        result.AddRange(list);

                    }

                }
            }
        }


        return result;
    }
}