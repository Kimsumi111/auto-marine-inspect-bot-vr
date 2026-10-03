using ShipRobot.Navigation;
using ShipRobot.LaneFollowing;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
var streak = new FreshDetectionStreak();
var losses = new FreshDetectionStreak();
Check(losses.Observe(1, true) == 1 && losses.Observe(1, true) == 1,
    "Repeated missing-line image counts only one loss");
Check(losses.Observe(2, true) == 2, "New missing-line image increments loss count");
losses.Reset();
Check(losses.Observe(3, true) == 1, "Recovered alignment resets consecutive losses");
Check(streak.Observe(1, true) == 1, "First camera observation counts once");
for (int i = 0; i < 100; i++)
    Check(streak.Observe(1, true) == 1, "Repeated Unity Updates cannot confirm the same image");
Check(streak.Observe(.5, true) == 1, "Older image cannot count");
Check(streak.Observe(2, true) == 2 && streak.Observe(3, true) == 3, "Three distinct images confirm the pair");
Check(streak.Observe(3, false) == 0, "Expired or invalid pair resets confirmation even on repeated image");
Check(streak.Observe(3, true) == 0, "Old image cannot re-establish confirmation");
Check(streak.Observe(4, true) == 1, "Fresh image begins a new streak");
Check(streak.Observe(5, false) == 0 && streak.Observe(6, true) == 1, "Bad camera frame breaks consecutive confirmation");
streak.Reset();
Check(streak.Observe(.1, true) == 1, "New search accepts timestamps after reset");
Check(streak.Observe(double.NaN, true) == 1 && streak.Observe(double.PositiveInfinity, true) == 1,
    "Nonfinite timestamps cannot count");
Console.WriteLine("PASS: repeated/older frames, consecutive images, expired detection, invalid-frame reset, search reset, nonfinite timestamps.");

static (float move, float turn) Control(float angle, float lateral)
    => BoundaryAlignmentControl.Calculate(angle, lateral, .42f, .5f, .22f, .40f);
var straight = Control(0, 0);
Check(Math.Abs(straight.move-.40f) < .00001f && straight.turn == 0, "Forward command equals configured constant");
var left = Control(-.3f, 0); var right = Control(.3f, 0);
Check(left.turn < 0 && right.turn > 0 && Math.Abs(left.move-right.move) < .00001f,
    "Both rotation directions move forward equally, never backwards");
Check(right.move == straight.move && Control(1f, 0).move == straight.move, "Forward command is independent of angle");
Check(Control(0, .2f).turn > 0 && Control(0, -.2f).turn < 0, "Parallel but displaced line still receives correction");
Check(Control(1.5f, 1).turn == .22f && Control(-1.5f, -1).turn == -.22f, "Rotation is bounded");
Check(Math.Abs(Control((float)Math.PI / 2, 0).move - .40f) < .00001f, "Perpendicular line retains constant forward command");
Check(Control((float)Math.PI, 0).move == .40f, "Large angles retain positive constant motion");
Check(Control(0, 1).move == straight.move, "Lateral steering does not increase forward speed");
Console.WriteLine("PASS: sine steering, constant forward speed, angle/lateral independence and command limits.");

var filter = new LaneMaskNoiseFilter();
var image = new bool[40 * 30];
image[0] = true; // Corner speck.
for (int y = 2; y < 5; y++) for (int x = 2; x < 5; x++) image[y * 40 + x] = true;
for (int y = 8; y < 24; y++) image[y * 40 + 12] = true;
for (int i = 0; i < 14; i++) image[(8 + i) * 40 + 20 + i] = true;
for (int y = 1; y < 6; y++) for (int x = 30; x < 35; x++) image[y * 40 + x] = true;
Check(filter.Apply(image, 40, 30, 24, 12, 3) == 10, "Only corner speck and compact small patch removed");
Check(image[8 * 40 + 12] && image[21 * 40 + 33], "Thin vertical and diagonal markings preserved");
Check(image[1 * 40 + 30], "Area threshold preserves large components");
Check(filter.Apply(image, 40, 30, 24, 12, 3) == 0, "Repeated mask filtering is stable");
var edges = new bool[12]; edges[3] = true; edges[4] = true;
Check(filter.Apply(edges, 4, 3, 2, 12, 3) == 2, "Image row edges do not wrap into connected components");
Check(filter.Apply(new bool[12], 4, 3, 24, 12, 3) == 0, "Empty mask is supported after resizing");
Console.WriteLine("PASS: small islands removed, thin/diagonal/large markings retained, no edge wrapping, buffer reuse/resize.");

string logPath = Path.Combine(Path.GetTempPath(), "navigation-log-test-" + Guid.NewGuid().ToString("N"), "drive.csv");
try
{
    using (var csv = new NavigationCsvLog(logPath, "event", "detail"))
    {
        csv.Write("fault", "lost, \"near\"\nfar");
        using var liveReader = new StreamReader(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        Check(liveReader.ReadToEnd().Contains("\"fault\",\"lost, \"\"near\"\"\nfar\""),
            "Fault details are escaped and flushed while the log is open");
    }
    Check(File.ReadAllText(logPath).StartsWith("\"event\",\"detail\""), "Log schema header is preserved");
}
finally { File.Delete(logPath); Directory.Delete(Path.GetDirectoryName(logPath)!); }
var stronger = BoundaryAlignmentControl.Calculate(.3f, .2f, .70f, .80f, .40f, .40f);
Check(stronger.turn > Control(.3f, .2f).turn && stronger.move == .40f,
    "Stronger alignment steering preserves constant forward command");
Console.WriteLine("PASS: CSV fault escaping, live flush, disposal and stronger steering.");
Check(BoundaryAlignmentControl.ExitHeadingTurn(-20, .2f) < 0 &&
      BoundaryAlignmentControl.ExitHeadingTurn(20, .2f) > 0,
    "Single-band heading correction turns toward the planned exit in both directions");
Check(BoundaryAlignmentControl.ExitHeadingTurn(2, .2f) == 0, "Exit heading deadband prevents unnecessary rotation");
Check(BoundaryAlignmentControl.ConstrainToExitHeading(.4f, -20, 15, .4f) < 0,
    "Opposing visual correction outside heading range is overridden toward exit");
Check(BoundaryAlignmentControl.ConstrainToExitHeading(.4f, -13, 15, .4f) == 0,
    "Visual steering farther outside the heading boundary is blocked");
Check(BoundaryAlignmentControl.ConstrainToExitHeading(-.2f, -13, 15, .4f) == -.2f,
    "Visual correction back toward exit is permitted");
Check(BoundaryAlignmentControl.ConstrainToExitHeading(.2f, -5, 15, .4f) == .2f,
    "Visual alignment inside permitted heading range remains available");
Console.WriteLine("PASS: exit-heading correction, direction symmetry, deadband and visual steering constraint.");

Check(IndoorProximity.Contains(0, 0, .45f), "NFC reader at zone centre is inside");
Check(IndoorProximity.Contains(.45f, 0, .45f), "NFC boundary is included");
Check(!IndoorProximity.Contains(.46f, 0, .45f), "NFC outside zone does not trigger");
Check(!IndoorProximity.Contains(.4f, .4f, .45f), "NFC uses a circle, not a square");
Check(IndoorProximity.Contains(-.2f, -.2f, .45f), "NFC works across negative map coordinates");
Check(!IndoorProximity.Contains(float.NaN, 0, .45f) && !IndoorProximity.Contains(0, 0, -1), "Invalid NFC samples/radii cannot trigger");
Console.WriteLine("PASS: virtual NFC centre, boundary, outside, diagonal, signed coordinates and invalid input.");
