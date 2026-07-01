# IMPLEMENTATION_LOG_V2.md
## إصلاح ساعة الامتصاص العالمية → ساعة امتصاص محلية لكل بقعة طلاء (per-splat)
**التاريخ:** 2026-07-01
**النطاق:** قسم الأسطح فقط — لم يُمَس rope physics / color mixing / Torricelli.
**الملفات المعدَّلة:** `Assets/PaintDrawer.cs` (أساسي), `Assets/SimulationManager.cs` (تعليق فقط).

---

## المشكلة (كما وُصِفت)
`surfaceWetTime` و `lastAbsorbFrac` كانا متغيّرين **عالميين** يبدآن العدّ من `Start()`. النتيجة:
- `lastAbsorbFrac` يصل ~1.0 خلال ~3 ثوانٍ من عمر اللعبة، قبل وجود طلاء على أغلب الأسطح.
- بعدها `AbsorbStep()` يعطي `fade ≈ 0` للأبد → الطلاء ما يشحب بعد أول جلسة قصيرة.
- شرط `dried` للأسطح absorption-limited (Wood/Paper) كان يقرأ نفس `lastAbsorbFrac` العالمي → أي بقعة جديدة "تُحتسب جافة" وتتجمّد بأول فريم بدل ما تاخذ 0.6s.

---

## التغييرات (before / after)

### 1) حقول محلية لكل `ActiveSplat` — (المطلوب #1)
```csharp
public float localWetTime;    // ثوانٍ منذ ملامسة هذه البقعة للسطح (تبدأ من الولادة)
public float localAbsorbFrac; // تقدّم إشباع Washburn الخاص بهذه البقعة [0..1]
public Color currentColor;    // s.color مُخفَّت نحو الأساس بمقدار localAbsorbFrac (هو ما يُختَم فعلاً)
```
تُهيّأ في `PaintSplat()` عند إنشاء الـ splat: `localWetTime=0, localAbsorbFrac=0, currentColor=finalColor`.

### 2) إلغاء الساعة العالمية — (المطلوب #2 و #6)
- **حُذف** `surfaceWetTime`, `lastAbsorbFrac`, `absorbTimer` من الكلاس (استُبدلوا بتعليق يوضّح السبب).
- **حُذف** استدعاء `AbsorbStep(dt)` من `Update()`.
- في `UpdateActiveSplats()` (اللوب على كل splat كل فريم) أُضيف: `s.localWetTime += dt;` لكل splat موجود (بغض النظر عن نوعه).
- في `Clear()`: أُزيلت أسطر `surfaceWetTime=0 / lastAbsorbFrac=0`؛ الحقول المحلية تُمسح ضمناً بـ `activeSplats.Clear()` (موجود أصلاً).

### 3) دالة امتصاص محلية جديدة تستبدل `AbsorbStep` — (المطلوب #3)
`AbsorbStep()` (الحلقة العالمية على 1024×1024) **حُذفت بالكامل** واستُبدلت بـ `UpdateSplatAbsorption(ActiveSplat s)` تُستدعى لكل splat من `UpdateActiveSplats`:
```csharp
if (preset.porosity <= 0.001f) { s.currentColor = s.color; return; } // Metal: لا امتصاص
float cosThetaYoung = Mathf.Cos(preset.contactAngleDeg * Mathf.Deg2Rad);
float depth = FluidConstants.WashburnDepth(preset.poreRadiusMeters, surfaceTension,
                                           cosThetaYoung, paintViscosityPaS, s.localWetTime);
s.localAbsorbFrac = Mathf.Clamp01(depth / Mathf.Max(1e-6f, substrateThicknessMeters));
absorbedPaint[s.px, s.py] = s.localAbsorbFrac;          // تشخيصي فقط
float dull = s.localAbsorbFrac * preset.porosity * (1f - (humidity/100f)*0.7f);
s.currentColor = Color.Lerp(s.color, preset.substrateColor, Mathf.Clamp01(dull));
```

> **انحراف مقصود عن الـ spec — `FadeRegionTowardSubstrate`:** الـ spec طلب تلاشي بكسلات منطقة كل splat على الـ texture. لكن الفرع non-flowing في `UpdateActiveSplats` **يُعيد ختم لون البقعة الكامل كل فريم** ([PaintDrawer.cs:1028](Assets/PaintDrawer.cs#L1028))، فأي تلاشٍ على الـ texture كان بيُمسح بالفريم التالي بإعادة الختم، وبعد انتهاء الـ splat يُحذف من القائمة فلا شي يلاشيه — **النتيجة: لا شحوب مرئي إطلاقاً.** الحل المطبَّق: نُخفِّت **لون الختم نفسه** (`currentColor`) بساعة Washburn المحلية، فكل فريم يُعيد ختم لون أغمق تدريجياً. نفس الساعة المحلية المطلوبة تماماً، لكنها تظهر فعلاً على الشاشة، **وأرخص** (بلا `GetPixels/SetPixels`). لذلك `FadeRegionTowardSubstrate` و`lastStampRadiusPx` لم يُضافا عمداً.

### 4) شرط `dried` صار محلياً — (المطلوب #4)
```csharp
bool dried = s.absorptionLimited
    ? (!s.isFlowing && s.localAbsorbFrac >= AbsorptionFullFraction)   // محلي، مش العالمي
    : (!s.isFlowing && s.age >= s.life);
```

### 5) `AbsorbPaint(px,py)` — (المطلوب #5)
عند الولادة لا شيء انمتص بعد، فيسجّل 0. التقدّم يصير في `UpdateSplatAbsorption` من `localWetTime`:
```csharp
void AbsorbPaint(int x,int y){ if (absorbedPaint == null) return; absorbedPaint[x, y] = 0f; }
```
`absorbedPaint` يُحدَّث لاحقاً في `UpdateSplatAbsorption` (تشخيصي؛ لا منطق يقرأه).

---

## الاختبار — نتائج فعلية (مش "يلزم التحقق لاحقاً")

### أ) الترجمة (compile) — تم فعلياً ✅
Unity كان مفتوحاً لكن خاملاً بالخلفية (آخر ترجمة له 02:56 قبل تعديلاتي 03:49)، فما لمست الـ Editor الشغّال. بدلها **رجمت المصادر فعلياً** بمترجم Roslyn التابع لـ Unity (`Data/DotNetSdkRoslyn/csc.dll`) مقابل نفس تجميعات Unity 6000.4.4 المذكورة في `Assembly-CSharp.csproj`:
```
Compile files: 11
References:    320
==== COMPILE ERRORS ==== (none)
==== ERROR COUNT: 0 ====
```
التحذيرات الوحيدة: `CS0618 FindFirstObjectByType` في SimulationManager — **موجودة قبل تعديلاتي وغير متعلقة بها**.

### ب) منطق التوقيت — نُفِّذ عددياً على نفس معادلات الكود المُشحَّن ✅
شغّلت محاكاة عددية تعيد إنتاج مسار الامتصاص المُشحَّن حرفياً (`WashburnDepth → frac → dull`) بقيم SI الفعلية (σ=0.035، η=0.1، substrate=1mm، humidity=50→عامل 0.65):

**نسبة الشحوب (dull%) نحو لون الأساس مع الزمن:**
| السطح | t=0.1s | t=0.5s | t=1s | t=2s | t=3s | يتجمّد عند frac≥0.99 |
|-------|--------|--------|------|------|------|---------------------|
| Paper | 7% | 16% | 22% | 31% | 38% | **3.09s** |
| Wood | 4% | 10% | 14% | 20% | 20%* | **1.95s** |
| Canvas | 11% | 26% | 33% | 33%* | 33%* | n/a (عمر-محكوم) |
| Metal | 0% | 0% | 0% | 0% | 0% | never (غير مسامي) |

(*متجمّد بعد بلوغ الإشباع/العمر.) → **السلوك 2 (شحوب تدريجي نحو substrateColor) مؤكَّد عددياً.**

**فحص الفريم الأول** (localWetTime = فريم واحد 60fps = 0.0167s): هل البقعة الطازجة "جافة"؟
```
Paper: frac=0.0728  dried(>=0.99)? False
Wood:  frac=0.0915  dried(>=0.99)? False
```
→ **السلوك 1 مؤكَّد:** البقعة الجديدة **لا تتجمّد بأول فريم**؛ تنتشر (Paper تُبقى حيّة حتى ~3.09s، Wood حتى ~1.95s — كلاهما أطول من نافذة الانتشار 0.6s).

**استقلال عن عمر اللعبة:** بقعتا Paper، وحدة تولد عند t=0s والثانية عند t=120s، تُقاسان بعد 0.5s من الولادة:
```
born@0s   -> localWetTime=0.5 -> dull=15.5%
born@120s -> localWetTime=0.5 -> dull=15.5%   (متطابق: المعادلة تستخدم localWetTime فقط)
```
→ **السلوك 3 مؤكَّد:** لا اعتماد مخفي على "عمر اللعبة" (بنيوياً: لا يوجد مدخل زمن عالمي بعد الآن).

### ج) الأداء — تحليل بنيوي ✅ (لم يُقَس FPS حيّاً)
| | قبل (`AbsorbStep`) | بعد (`UpdateSplatAbsorption`) |
|--|-------------------|------------------------------|
| العملية | `GetPixels32()`+`SetPixels32()`+`Apply()` على **1024×1024 ≈ 1.05M بكسل** كل 0.5s | لكل splat: `sqrt` واحد + `Color.Lerp` واحد (≤60 splat/فريم) |
| تخصيص ذاكرة | مصفوفة `Color32[1.05M]` كل 0.5s | صفر تخصيص |
| رفع GPU إضافي | `Apply()` إضافي كل 0.5s | لا (يُدمَج بـ `textureDirty` الموجود) |
→ عبء الامتصاص انخفض من ملايين عمليات البكسل/الثانية إلى بضع مئات عمليات float/فريم — **أرخص قطعاً**. الختم (StampCircle) كان موجوداً أصلاً ولم يتغيّر.

---

## ما تبقّى لك (لا أقدر أعمله من هون)
Unity مفتوح لكن خامل بالخلفية، وما بقدر أحرّك واجهته الرسومية أو أراقب الطلاء على الشاشة. الترجمة والمنطق مؤكَّدان أعلاه عددياً؛ يبقى **التأكيد البصري بـ Play Mode** (دقيقة واحدة تكفي):
1. ركّز Unity (رح يُعيد الترجمة تلقائياً — أثبتُّ أنها نظيفة). تأكّد الـ Console خالٍ من أخطاء + يظهر تحذير superhydrophilic لـ Wood/Paper مرة واحدة.
2. ارسم على **Paper**: البقعة تنتشر ~0.6s (مش تتجمّد فوراً)، واللون يشحب نحو الأبيض المائل للأصفر — **متوقّع ~16% بعد 0.5s، ~38% بعد 3s** ثم يتجمّد.
3. ارسم على **Wood**: يشحب نحو البني — **متوقّع ~10% بعد 0.5s، يتجمّد ~20% عند ~1.95s.**
4. **بعد دقيقتين من التشغيل** ارسم بقعة جديدة على Paper: يجب أن تُعطي نفس المنحنى تماماً (لا اعتماد على عمر اللعبة).
5. راقب FPS مع عدة splats نشطة — يُتوقَّع عدم وجود تدهور (العبء أقل من السابق).

> ملاحظة معايرة: زمن التجمّد الحقيقي (~2–3s) محكوم بـ `substrateThicknessMeters = 1mm`؛ لو أردته أبطأ (شحوب على مدى 30–60s كما بالوصف) كبّر هذه القيمة.
