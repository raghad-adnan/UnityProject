using System.Collections.Generic;
using UnityEngine;


public class SpatialHash
{

    private readonly float cellSize;


    private readonly Dictionary<Vector3Int, List<PaintParticle>> cells;


    public SpatialHash(float size)
    {
        cellSize = size;

        cells =
        new Dictionary<Vector3Int, List<PaintParticle>>();
    }



    private Vector3Int HashPosition(Vector3 position)
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

        Vector3Int hash =
        HashPosition(particle.position);



        if(!cells.TryGetValue(hash, out List<PaintParticle> bucket))
        {

            bucket =
            new List<PaintParticle>();

            cells.Add(hash, bucket);

        }



        bucket.Add(particle);

    }





    public List<PaintParticle> GetNeighbors(Vector3 position)
    {

        List<PaintParticle> neighbors =
        new List<PaintParticle>();



        Vector3Int center =
        HashPosition(position);




        // 27 خلية حول الجزيء

        for(int x=-1; x<=1; x++)
        {

            for(int y=-1; y<=1; y++)
            {

                for(int z=-1; z<=1; z++)
                {


                    Vector3Int key =
                    center +
                    new Vector3Int(x,y,z);



                    if(cells.TryGetValue(key,
                    out List<PaintParticle> particles))
                    {

                        neighbors.AddRange(particles);

                    }


                }

            }

        }


        return neighbors;

    }


}