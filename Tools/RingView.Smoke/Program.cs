using RingView.Smoke;

// Live check of the production stack against the real Ring service, with the installed app's
// token. Quit the installed RingView first: both rotate the same token.
//
//   dotnet run --project Tools/RingView.Smoke -- [minutes] [keep-alive seconds] [--decode] [--audio N]
//
// Prints camera positions, never ids or names, and never a token.

int minutes = args.Length > 0 && int.TryParse(args[0], out int m) ? m : 5;
TimeSpan? keepAlive = args.Length > 1 && int.TryParse(args[1], out int k) ? TimeSpan.FromSeconds(k) : null;
bool decode = args.Contains("--decode");
int audioIndex = Array.IndexOf(args, "--audio");
int? audioCamera = audioIndex >= 0 && audioIndex + 1 < args.Length && int.TryParse(args[audioIndex + 1], out int a) ? a : null;
return await ProductionSmoke.RunAsync(TimeSpan.FromMinutes(minutes), keepAlive, decode, audioCamera);
