using System.Collections.Generic;
using UnityEngine;


public class SPHSolver
{

    public float smoothingRadius = 0.18f;


    public float restDensity = 1f;


    public float stiffness = 0.5f;
    public float particleMass = 0.02f;
    public float viscosity = 10f;



    float PI = Mathf.PI;



    // Poly6 Kernel
    float Poly6(float distance)
    {

        if(distance >= smoothingRadius)
            return 0f;


        float h2 =
            smoothingRadius *
            smoothingRadius;


        float r2 =
            distance *
            distance;



        float coefficient =
            315f /
            (64f *
            PI *
            Mathf.Pow(smoothingRadius,9));



        return coefficient *
            Mathf.Pow(h2-r2,3);

    }




    // Spiky gradient
    Vector3 SpikyGradient(
        Vector3 direction,
        float distance)
    {

        if(distance <= 0 ||
           distance >= smoothingRadius)
            return Vector3.zero;



        float coefficient =
            -45f /
            (PI *
            Mathf.Pow(smoothingRadius,6));



        float value =
            coefficient *
            Mathf.Pow(
                smoothingRadius-distance,
                2);



        return direction.normalized * value;

    }





    // Viscosity Laplacian
    float ViscosityLaplacian(float distance)
    {

        if(distance >= smoothingRadius)
            return 0f;



        float coefficient =
            45f /
            (PI *
            Mathf.Pow(smoothingRadius,6));



        return coefficient *
            (smoothingRadius-distance);

    }






    public float CalculateDensity(
        PaintParticle particle,
        List<PaintParticle> neighbors)
    {

        float density = 0f;



        foreach(PaintParticle other in neighbors)
        {

            float distance =
                Vector3.Distance(
                    particle.position,
                    other.position);



            density +=particleMass *Poly6(distance);

        }



        return Mathf.Max(
            density,
            0.0001f);

    }





    public Vector3 CalculatePressureForce(
        PaintParticle particle,
        List<PaintParticle> neighbors)
    {


        Vector3 force =
            Vector3.zero;



        float density =
            CalculateDensity(
                particle,
                neighbors);



        float pressure =stiffness *(density - restDensity) /restDensity;




        foreach(PaintParticle other in neighbors)
        {

            if(other == particle)
                continue;



            Vector3 direction =
                particle.position -
                other.position;



            float distance =
                direction.magnitude;



            Vector3 gradient =
                SpikyGradient(
                    direction,
                    distance);



            force -=gradient *pressure *particleMass / density;

        }



        return force;

    }





    public Vector3 CalculateViscosityForce(
        PaintParticle particle,
        List<PaintParticle> neighbors)
    {

        Vector3 force =
            Vector3.zero;



        foreach(PaintParticle other in neighbors)
        {

            if(other == particle)
                continue;



            float distance =
                Vector3.Distance(
                    particle.position,
                    other.position);



            float kernel =
                ViscosityLaplacian(
                    distance);



            force +=
                (other.velocity -
                particle.velocity)
                *
                kernel *
                viscosity;

        }



        return force;

    }

}