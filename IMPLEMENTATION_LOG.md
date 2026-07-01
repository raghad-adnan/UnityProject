# IMPLEMENTATION_LOG.md
## تنفيذ قرارات مراجعة الأسطح (SURFACE_PHYSICS_AUDIT.md)
**التاريخ:** 2026-07-01
**النطاق:** قسم الأسطح الأربعة فقط (Metal / Wood / Canvas / Paper). لم يُمَس rope physics، color mixing، أو Torricelli's law.
**الملفات المعدَّلة:** `Assets/SurfacePreset.cs`, `Assets/FluidConstants.cs`, `Assets/PaintDrawer.cs`, `Assets/SimulationManager.cs`

---

## 1) لون الأساس (substrate color) لكل سطح — يعالج ❷ بالـ audit

### `SurfacePreset.cs`
- **أُضيف حقل** `public Color substrateColor;` للـ struct + بارامتر `Color substrate` بالـ constructor + إسناده.
- **قيم الأساس الجديدة** (مدمجة بتعريفات الـ presets):

| السطح | substrateColor (RGB) | Hex |
|-------|----------------------|-----|
| Metal | (0.690, 0.690, 0.710) | #B0B0B5 رمادي فضي |
| Wood | (0.545, 0.353, 0.169) | #8B5A2B بني طبيعي |
| Canvas | (0.980, 0.970, 0.940) | كريمي ≈ أبيض (كما كان تقريباً) |
| Paper | (0.990, 0.980, 0.930) | أبيض مائل للأصفر (كما كان تقريباً) |

### `PaintDrawer.cs` — أماكن استبدال الأبيض الموحَّد بالـ substrateColor
| الموقع | قبل | بعد |
|--------|-----|-----|
| `Clear()` تهيئة الـ texture | `cols[i] = Color.white` | `cols[i] = preset.substrateColor` (مع guard إذا `a<=0` يرجع أبيض) |
| `PaintSplat()` Beer-Lambert base | `Color.Lerp(Color.white, p.color, opacity)` | `Color.Lerp(preset.substrateColor, p.color, opacity)` — الطلاء الرقيق يكشف لون السطح الحقيقي |
| `AbsorbStep()` fade الامتصاص | يتلاشى نحو 255 (أبيض) | يتلاشى نحو `preset.substrateColor` مع `Mathf.Clamp(...,0,255)` (لأن الأساس قد يكون أغمق من البكسل) |
| `GetPaintAreaCoverage()` عتبة "مطلي" | `r<245 || g<245 || b<245` | `|channel − substrate| > 12` لكل قناة |

> **ملاحظة مهمة (أثر جانبي تم إصلاحه):** بدون تعديل `GetPaintAreaCoverage`، كان السطح المعدني الرمادي الفارغ سيُحتسب 100% تغطية (لأن 176 < 245). صار القياس نسبةً للون الأساس بدل الأبيض الثابت.

---

## 2) cosθ_Young بدل cosθ*(Wenzel) داخل Lucas-Washburn + توحيد الامتصاص — يعالج ❺ + تناقض #3

### `PaintDrawer.cs` → `AbsorbStep()`
- **قبل:** `cosThetaStar = WenzelCos(r, youngRad)` ثم يُمرَّر لـ `WashburnDepth(...)`.
- **بعد:** `cosThetaYoung = Mathf.Cos(youngRad)` يُمرَّر لـ `WashburnDepth(...)`.
- **السبب الفيزيائي:** الصعود الشعري داخل المسام تحكمه الزاوية الجوهرية للجدار (Young)، لا الزاوية الظاهرية الماكروسكوبية (Wenzel). Wenzel تصف البصمة الخارجية فقط.
- **أثر التصحيح (تقليل التضخيم):** Wood ≈ −74% ، Canvas ≈ −40% بمعدّل الامتصاص (يرجع للقيم الفيزيائية الصحيحة). Metal/Paper بلا فرق يُذكر.

### توحيد نموذجَي الامتصاص (مصدر حقيقة واحد)
- **مصدر الحقيقة الوحيد** = `lastAbsorbFrac` (تقدّم إشباع Washburn العالمي، يتقدّم حصراً داخل `AbsorbStep`).
- `AbsorbPaint(x,y)`: **حُذف** نموذجه المستقل (`porosity*(1-current)*dt`) وصار **مستهلِكاً**: `absorbedPaint[x,y] = Mathf.Clamp01(lastAbsorbFrac)`.
- النتيجة: لا يمكن للمسارين أن يختلفا حول درجة الإشباع (كان تناقض #3 بالـ audit).

### `SimulationManager.cs` → `DrawSurfacePhysicsReadout()`
- قراءة "Washburn depth (1 s)" صارت تستخدم `cosYoung` بدل `cosStar` لتطابق `AbsorbStep`. (عرض زاوية Wenzel `theta_Wenzel` بقي كما هو لأنه عرض صحيح للزاوية الظاهرية.)

---

## 3) نموذج Tanner الهجين حسب θ* — يعالج ❶

### `FluidConstants.cs`
- **أُضيفت** `TannerRelaxTimeDeGennes(eta, gamma, R, thetaEqRad)` = `eta·R/(gamma·θ³)` مع أرضية `θ ≥ 0.01 rad`.
  - مصدر: de Gennes (1985), Rev. Mod. Phys. 57, 827 — الزمن الذي يقترن فعلاً بأُس Tanner 1/10.
- `TannerRelaxTime` القديم (gravity-lubrication) **بقي للمرجع فقط** مع تعليق "SUPERSEDED" — لم يعد يُستدعى.

### `PaintDrawer.cs` → `PaintSplat()` تسجيل الـ ActiveSplat
- حُسبت `thetaEqRad = acos(clamp(cosThetaStar))` و `absorptionLimited = thetaEqRad <= 0.01 rad`.
- **النظام الأول — θ*>0 (Metal, Canvas):** `tv = TannerRelaxTimeDeGennes(mu, surfaceTension, Rmeters, thetaEqRad)`.
- **النظام الثاني — θ*=0 (Wood, Paper):** `tv = AbsorptionSpreadVisualSeconds = 0.6f` (ثابت بصري موثَّق، **ليس** t_v فيزيائي).
- حقول جديدة بـ `ActiveSplat`: `thetaEqRad`, `absorptionLimited`.
- ثوابت جديدة بالكلاس: `AbsorptionSpreadVisualSeconds=0.6`, `AbsorptionFullFraction=0.99`, `CompleteWettingAngleRad=0.01`.

### `PaintDrawer.cs` → `UpdateActiveSplats()` شرط الإنهاء
- **قبل:** `expired = (!isFlowing && age >= life) || offCanvas || age >= FlowSafetySeconds`.
- **بعد:** للأسطح absorption-limited يصبح الإنهاء محكوماً بالإشباع:
  ```
  dried = absorptionLimited ? (!isFlowing && lastAbsorbFrac >= 0.99)
                            : (!isFlowing && age >= life);
  ```
  أي: Wood/Paper تستمر بالانتشار حتى يتشبّع امتصاص Washburn محلياً، ثم تتجمّد — بديل real-time موثَّق عن t_v→∞ النظري. (تنطبق θ³ القسمة على شبه صفر هنا، فاستُبعدت صراحةً.)

> **التطابق مع الـ audit:** Metal (θ*=12.6°) و Canvas (θ*=8.1°) → de Gennes. Wood و Paper (θ*=0° بعد Wenzel clamp) → absorption-limited. مطابق تماماً للقرار.

---

## 4) تصحيح خشونة على عتبة Splash (K) — يعالج ❸ + تناقض #1

### `FluidConstants.cs`
- ثوابت جديدة: `SplashRoughnessRefUm = 25f`, `SplashRoughnessAlpha = 0.4f`.
- دالة جديدة: `SplashThresholdRough(raMicrometres) = SplashThresholdK * (1 − alpha·min(Ra/Ra_ref, 1))`.
- **تعليق صريح بالكود:** «هذا تصحيح هندسي تقريبي وليس معادلة مستخرجة حرفياً من ورقة محكّمة — alpha معاير يدوياً» (اتجاه عام فقط من Mundo 1995 / Rioboo 2002، بلا اقتباس صيغة مغلقة). **لم يُكتب أي استشهاد وهمي.**

### `PaintDrawer.cs` → `PaintSplat()`
- **قبل:** `if (K > FluidConstants.SplashThresholdK)`.
- **بعد:** `float Kthreshold = SplashThresholdRough(preset.arithmeticRoughnessUm); if (K > Kthreshold)`.

### إصلاح اتّساقي إضافي — `ScatterDroplets()`
- كانت تحسب طاقة القذف من الثابت 57.7؛ مع عتبة مُخفَّضة كان `1 − 57.7/K` يصير سالباً → مدى قذف صفري للأسطح الخشنة.
- صارت تأخذ `kThreshold` كبارامتر وتستخدمه: `kExcess = clamp01(1 − kThreshold/K)`. (نفس عتبة التحفيز.)

### `SimulationManager.cs` → القراءة الحية
- سطر K صار يعرض العتبة الفعلية للسطح:
  `K (Stow-Hadfield) = {K} / Kc = {kThreshold} -> SPLASH/deposition`.

### القيم الجديدة لعتبة الـ Splash (alpha=0.4, Ra_ref=25μm) — تحقّق
| السطح | Ra (μm) | K_eff المحسوب | نطاق الـ audit المطلوب | ✓ |
|-------|---------|---------------|------------------------|---|
| Metal | 0.5 | 57.7×0.992 = **57.24** | ≈57.7 (بلا تغيير) | ✓ |
| Paper | 4.0 | 57.7×0.936 = **54.0** | 52–55 | ✓ |
| Wood | 6.0 | 57.7×0.904 = **52.2** | 46–52 (عند الحد الأعلى) | ✓≈ |
| Canvas | 20.0 | 57.7×0.680 = **39.2** | 35–43 | ✓ |

---

## 5) توثيق حالة Wenzel clamp (تشخيصي فقط، بدون تغيير سلوك) — يعالج ❹

### `FluidConstants.cs` → `WenzelCos()`
- وُسِّع التعليق ليوضح صراحةً أن `r·cosθ ≥ 1` يعني انتقالاً لنظام complete-wetting / superhydrophilic خارج صلاحية Wenzel الكلاسيكي (مرجع Bico, Thiele & Quéré 2002). ليس خطأ. السلوك (clamp) لم يتغيّر.

### `PaintDrawer.cs` → `Start()`
- أُضيف **تحذير لمرة واحدة عند بداية اللعبة** (ليس كل فريم): يمرّ على الأسطح الأربعة، وإذا `r·cosθ_Young > 1` يطبع:
  ```
  Debug.LogWarning("[Surface] {name}: r*cos(theta_Young) = {x:F3} > 1 -> complete-wetting (superhydrophilic) regime, outside classic Wenzel validity; theta* clamped to 0.")
  ```
- الأسطح التي ستُطلق التحذير: **Wood** (1.033) و **Paper** (1.087).

---

## الاختبارات

- **لا يوجد test suite خاص بالمشروع** (Edit-Mode/Play-Mode). كل ملفات الـ `*Test*.cs` و`*.asmdef` الموجودة تخص حِزم Unity تحت `Library/PackageCache` فقط، لا كود المشروع.
- **Unity Editor لم يكن مشغّلاً** وقت التنفيذ (MCP `connected: false`)، فتعذّر تشغيل recompile/Play-Mode فعلي.
- **التحقق الذي جرى فعلاً:** مراجعة ثابتة (static review) لكل تعديل، تتبّع نطاق كل متغيّر (scope)، إعادة حساب قيم عتبة الـ Splash يدوياً ومطابقتها لنطاقات الـ audit (الجدول أعلاه). **يلزم فتح Unity وتشغيل Play Mode للتأكيد النهائي بصرياً وللتأكد من خلوّ الـ Console من أخطاء ترجمة.**

## التحقق المطلوب منك بعد فتح Unity
1. افتح المشهد وشغّل Play — تأكّد أن الـ Console خالٍ من compile errors، وأن تحذير الـ superhydrophilic يظهر لـ Wood و Paper مرة واحدة.
2. بدّل السطح إلى Metal: الخلفية يجب أن تكون رمادية، وإلى Wood: بنّية.
3. راقب readout الـ Paint tab: `Kc` يتغيّر حسب السطح (Metal≈57، Canvas≈39).
