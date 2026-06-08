using UnityEngine;

public enum SurfaceType
{
    Canvas,
    Wood,
    Metal,
    Paper
}

public class PaintPhysics : MonoBehaviour
{
    [Header("Analysis")]
    public float CurrentFlow;
    public float CurrentRe;
    public float CurrentWe;
    public float CurrentSpeed;
    public float CurrentCa;
    public float CurrentOh;
    public float CurrentFr;
    public float splashTimer;
    public float density = 1000f;
    public float surfaceTension = 0.072f;
    public float temperature = 25f;
    public Transform paintPoint;

    public Renderer canvasRenderer;
    public PendulumMotion bucketMotion;
    public SurfaceType surface;
    private Texture2D texture;

    [Header("Paint Physics")]
    public float holeDiameter = 0.0015f;
    public float gravity = 9.81f;
    public float viscosity = 1f;
    public float Cd = 0.7f;
    public float startDelay = 0.2f;
    public float timer = 0f;
    public Color paintColor = Color.red;
    public float baseAbsorptionFactor;
    private Vector2 lastUV;
    private bool hasLast = false;
    [Header("Sloshing")]
public float sloshStrength = 0.15f;
private float sloshAngle = 0f;
    [Header("Paint Reservoir")]
    [Range(0, 100)]
    public float humidity = 50f;
    public float paintVolume = 5f;
    private float spreadFactor = 1f;
    private float absorptionFactor = 0f;
    public float bucketRadius = 0.1f;

    [Header("Projectile Paint")]
    public float projectileFactor = 0.15f;
    public float roughness = 0f;

    [Header("Drawing Settings")]
    public float drawRadiusMultiplier = 20f;  // تحكم بحجم الخط (زودها عشان خط أثخن)

    void Start()
    {
        texture = new Texture2D(1024, 1024);
        texture.wrapMode = TextureWrapMode.Clamp;
        canvasRenderer.material.mainTexture = texture;

        switch (surface)
        {
            case SurfaceType.Canvas:
                spreadFactor = 1.2f;
                absorptionFactor = 0.04f;
                roughness = 0.3f;
                break;
            case SurfaceType.Wood:
                spreadFactor = 0.8f;
                absorptionFactor = 0.02f;
                roughness = 0.5f;
                break;
            case SurfaceType.Metal:
                spreadFactor = 1.8f;
                absorptionFactor = 0.001f;
                roughness = 0f;
                break;
            case SurfaceType.Paper:
                spreadFactor = 2.2f;
                absorptionFactor = 0.08f;
                roughness = 0.2f;
                break;
        }
        baseAbsorptionFactor = absorptionFactor;
        Clear();
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (paintVolume <= 0f) return;
        if (timer < startDelay) return;

        float bucketArea = Mathf.PI * bucketRadius * bucketRadius;
        splashTimer += Time.deltaTime;

        float h = paintVolume / bucketArea;
        float holeHeightFromBottom = 0.04f;
        if (h <= holeHeightFromBottom)
        {
            return;  // الطلاء ما يخرج
        }
        float bucketSpeed = bucketMotion.velocity.magnitude;
        float sloshAngle = Mathf.Sin(Time.time * 5f) * bucketSpeed * sloshStrength;
        float sloshHeight = Mathf.Sin(sloshAngle) * 0.02f;
        h += sloshHeight;
        h = Mathf.Max(0.01f, h);
        float pendulumEffect =
        Mathf.Abs(bucketMotion.GetTangentialAcceleration())
        /
        gravity;

        h *=
        (1f + pendulumEffect * 0.3f); float A = Mathf.PI * Mathf.Pow(holeDiameter / 2f, 2);

        // التدفق الأساسي
        float baseFlow = Cd * A * Mathf.Sqrt(2f * gravity * h);

        // تأثير الحرارة على اللزوجة
        float effectiveViscosity = viscosity / (1f + temperature * 0.02f);

        // سرعة الدلو
        float speed = bucketMotion.velocity.magnitude;

        // حساب الأعداد اللابعدية
        float Re = density * speed * holeDiameter / effectiveViscosity;
        float We = density * speed * speed * holeDiameter / surfaceTension;
        float breakupFactor =

Mathf.Clamp01(
(We - 100f) /
400f
);
        float Fr =
speed *
speed /
(gravity * holeDiameter); 
        float Ca = effectiveViscosity * speed / surfaceTension;
        float Oh = effectiveViscosity / Mathf.Sqrt(density * surfaceTension * holeDiameter);

        // تعديل الخشونة
        if (Re > 2000f) roughness = 0.8f;
        else if (Re > 1000f) roughness = 0.4f;
        else roughness = 0.1f;

        if (Oh > 0.15f) roughness *= 0.5f;
        else if (Oh < 0.05f) roughness *= 1.5f;

        // التدفق النهائي
        float viscosityCorrection =
        1f /
        (1f + effectiveViscosity);

        float tensionFactor =
 bucketMotion.GetTensionForce()
 /
 (bucketMotion.mass * gravity);

        float Q =
        (baseFlow / effectiveViscosity)
        *
        (1f + speed * 0.3f)
        *
        (1f + (tensionFactor - 1f) * 0.2f);
        float jetVelocity =
Cd *
Mathf.Sqrt(
2f * gravity * h
);
        CurrentRe = Re;
        CurrentWe = We;
        CurrentSpeed = speed;

        // تأثير الرطوبة
        float humidityEffect = 1f - (humidity / 100f) * 0.7f;
        absorptionFactor = baseAbsorptionFactor * humidityEffect;
        CurrentCa = Ca;
        CurrentOh = Oh; CurrentFr = Fr;

        // تناقص حجم الطلاء
        paintVolume -= Q * Time.deltaTime;
        paintVolume = Mathf.Max(0f, paintVolume);

        // راي كاست للرسم
        Vector3 predictedOrigin = paintPoint.position + bucketMotion.velocity * projectileFactor;


        Ray ray =
new Ray(
predictedOrigin,
Vector3.down
); RaycastHit hit;

        if (Physics.Raycast(ray, out hit))
        {
            Vector2 uv = hit.textureCoord;

            if (!hasLast)
            {
                lastUV = uv;
                hasLast = true;
                return;
            }

            int px = (int)(uv.x * texture.width);
            int py = (int)(uv.y * texture.height);

            // حساب حجم النقطة المرسومة
            float jetDiameter =
            Mathf.Sqrt(
            (4f * Q) /
            (Mathf.PI * speed + 0.0001f)
            ); float temperatureSpread = 1f + (temperature - 25f) * 0.01f;
            jetDiameter *= temperatureSpread;

            if (Ca > 1f) jetDiameter *= 1.3f;
            else if (Ca < 0.1f) jetDiameter *= 0.8f;

            // حجم الرسم - زود الـ multiplier عشان خط أثخن
            int drawRadius = Mathf.RoundToInt(jetDiameter * drawRadiusMultiplier * spreadFactor);
            drawRadius =
            Mathf.RoundToInt(
            drawRadius *
            (1f - breakupFactor * 0.4f)
            ); drawRadius = Mathf.Clamp(drawRadius, 1, 1);
            // رسم الخط بين النقطة السابقة والحالية
            DrawLine(lastUV, uv, drawRadius);

            // تنقيط حسب سرعة ويبر
            if (We > 250f && splashTimer > 0.15f)
            {
                CreateSplash(px, py);
                splashTimer = 0f;
            }
            if (We > 500f && splashTimer > 0.4f)
            {
                CreateCrownSplash(px, py);
                splashTimer = 0f;
            }

            lastUV = uv;
            AbsorbPaint();
            texture.Apply();
        }
    }

    void AbsorbPaint()
    {
        // تحسين الأداء: نشتغل كل 5 فريمات
        if (Time.frameCount % 5 != 0) return;

        for (int x = 0; x < texture.width; x += 8)
        {
            for (int y = 0; y < texture.height; y += 8)
            {
                Color c = texture.GetPixel(x, y);
                float decay = Mathf.Exp(-absorptionFactor * Time.deltaTime * 5f);
                c *= decay;
                texture.SetPixel(x, y, c);
            }
        }
    }

    void DrawLine(Vector2 a, Vector2 b, int r)
    {
        // عدد النقاط بين النقطتين (كل ما زاد، الخط أنعم)
        float distance = Vector2.Distance(a, b);
        int steps = Mathf.Max(2, Mathf.RoundToInt(distance * 200f));

        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector2 uv = Vector2.Lerp(a, b, t);
            int x = (int)(uv.x * texture.width);
            int y = (int)(uv.y * texture.height);
            DrawDot(x, y, r);
        }
    }

    void DrawDot(int cx, int cy, int r)
    {
        for (int x = -r; x <= r; x++)
        {
            for (int y = -r; y <= r; y++)
            {
                // حساب المسافة من المركز عشان نرسم دائرة
                if (x * x + y * y > r * r) continue;

                int px = cx + x;
                int py = cy + y;

                // إضافة عشوائية حسب الخشونة
                if (roughness > 0)
                {
                    px += Random.Range(-Mathf.RoundToInt(roughness), Mathf.RoundToInt(roughness) + 1);
                    py += Random.Range(-Mathf.RoundToInt(roughness), Mathf.RoundToInt(roughness) + 1);
                }

                if (px >= 0 && px < texture.width && py >= 0 && py < texture.height)
                {
                    Color oldColor = texture.GetPixel(px, py);
                    float mixAmount = 0.5f * (1f - absorptionFactor);
                    Color newColor = Color.Lerp(oldColor, paintColor, mixAmount);
                    texture.SetPixel(px, py, newColor);
                    SpreadPaint(
px,
py
);
                }
            }
        }
    }
    void SpreadPaint(
int cx,
int cy
)
    {
        if (Random.value > 0.02f)
            return;

        for (int i = 0; i < 4; i++)
        {
            int nx =
            cx +
            Random.Range(-1, 2);

            int ny =
            cy +
            Random.Range(-1, 2);

            if (
                nx >= 0 &&
                nx < texture.width &&
                ny >= 0 &&
                ny < texture.height
            )
            {
                texture.SetPixel(
                nx,
                ny,
                paintColor
                );
            }
        }
    }
    void DrawEllipse(int cx, int cy, int rx, int ry)
    {
        for (int x = -rx; x <= rx; x++)
        {
            for (int y = -ry; y <= ry; y++)
            {
                float dx = x / (float)rx;
                float dy = y / (float)ry;
                if (dx * dx + dy * dy <= 1f)
                {
                    int px = cx + x;
                    int py = cy + y;
                    if (px >= 0 && px < texture.width && py >= 0 && py < texture.height)
                    {
                        texture.SetPixel(px, py, paintColor);
                    }
                }
            }
        }
    }

    void CreateSplash(int cx, int cy)
    {
        int droplets = Mathf.Clamp(Mathf.RoundToInt(CurrentWe / 100f), 2, 8);
        int radius = Mathf.Clamp(Mathf.RoundToInt(CurrentWe / 150f), 1, 4);

        for (int i = 0; i < droplets; i++)
        {
            int rx = Random.Range(-radius, radius + 1);
            int ry = Random.Range(-radius, radius + 1);
            int px = cx + rx;
            int py = cy + ry;
            if (px >= 0 && px < texture.width && py >= 0 && py < texture.height)
            {
                texture.SetPixel(px, py, paintColor);
            }
        }
    }

    void CreateCrownSplash(int cx, int cy)
    {
        int spikes = Mathf.Clamp(Mathf.RoundToInt(CurrentWe / 60f), 12, 30);

        for (int i = 0; i < spikes; i++)
        {
            float angle = i * Mathf.PI * 2f / spikes;
            int px = cx + Mathf.RoundToInt(Mathf.Cos(angle) * Mathf.Clamp(CurrentWe / 20f, 5f, 20f));
            int py = cy + Mathf.RoundToInt(Mathf.Sin(angle) * Mathf.Clamp(CurrentWe / 20f, 5f, 20f));
            DrawDot(px, py, 1);
        }
    }

    public void Clear()
    {
        Color[] c = new Color[1024 * 1024];
        for (int i = 0; i < c.Length; i++) c[i] = Color.white;
        texture.SetPixels(c);
        texture.Apply();
        hasLast = false;
        timer = 0f;
    }

    public void SavePainting()
    {
        string filename = $"Painting_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
        byte[] bytes = texture.EncodeToPNG();
        System.IO.File.WriteAllBytes(Application.dataPath + "/" + filename, bytes);
        Debug.Log("تم حفظ اللوحة في: " + filename);
    }

    // اختصارات الكيبورد
    void OnGUI()
    {
        if (Event.current.type == EventType.KeyDown)
        {
            if (Event.current.keyCode == KeyCode.S)
            {
                SavePainting();
            }
            if (Event.current.keyCode == KeyCode.R)
            {
                Clear();
            }
        }
    }
}