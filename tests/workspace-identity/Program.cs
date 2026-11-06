using TabletDeck;

var passed=0;
void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
void Reject(Action action,string label){try{action();throw new Exception("Unexpected acceptance: "+label);}catch(ArgumentException){Check(true,label);}}

Check(WorkspaceIdentity.StartupError(false,null) is null,"ordinary unpackaged production startup is retained");
Check(WorkspaceIdentity.StartupError(false,"") is null,"empty package identity remains unpackaged");
var error=WorkspaceIdentity.StartupError(false,"OpenAI.Codex_1.0.0.0_x64__fixture");
Check(error is not null&&error.Contains("Windows shortcut",StringComparison.Ordinal)&&error.Contains("--data-dir",StringComparison.Ordinal),"inherited packaged context gives both safe recovery choices");
Check(WorkspaceIdentity.StartupError(true,"OpenAI.Codex_1.0.0.0_x64__fixture") is null,"explicit isolated data path remains usable from a packaged development host");
Check(WorkspaceIdentity.StartupError(true,null) is null,"normal explicit data path remains usable");
Check(WorkspaceIdentity.StartupError(false,"AnotherLauncher_1.0.0.0_x64__fixture")==error,"guard covers arbitrary inherited package identities rather than one developer app");
Check(!error!.Contains("OpenAI",StringComparison.Ordinal)&&!error.Contains("fixture",StringComparison.Ordinal),"startup message does not expose inherited package details");

Check(WorkspaceIdentity.StartupError(true) is null,"explicit path bypasses native context probing without opening data");
var actual=WorkspaceIdentity.StartupError(false);
Check(actual is null||actual.Contains("DeskWing",StringComparison.Ordinal),"native context probe returns a safe decision without opening configuration");

Check(WorkspaceIdentity.ExplicitDataDirectory([]) is null,"data directory remains implicit only when the option is absent");
Check(WorkspaceIdentity.ExplicitDataDirectory(["--no-browser","--http-port","9876"]) is null,"other command-line options do not authorize an implicit packaged workspace");
var absolute=Path.Combine(Path.GetTempPath(),"deskwing-explicit-workspace-fixture");
Check(WorkspaceIdentity.ExplicitDataDirectory(["--data-dir",absolute])==Path.GetFullPath(absolute),"an absolute explicit directory retains its intended destination");
Check(WorkspaceIdentity.ExplicitDataDirectory(["--no-browser","--data-dir",absolute,"--http-port","9876"])==Path.GetFullPath(absolute),"valid surrounding options retain the same explicit destination");
Check(WorkspaceIdentity.ExplicitDataDirectory(["--data-dir","relative-fixture"])==Path.GetFullPath("relative-fixture"),"relative explicit directory is resolved without falling back to AppData");
Reject(()=>WorkspaceIdentity.ExplicitDataDirectory(["--data-dir"]),"trailing data-dir cannot claim an explicit path");
Reject(()=>WorkspaceIdentity.ExplicitDataDirectory(["--data-dir","--no-browser"]),"another option cannot become a data directory");
Reject(()=>WorkspaceIdentity.ExplicitDataDirectory(["--data-dir",""]),"empty data-dir cannot bypass the context guard");
Reject(()=>WorkspaceIdentity.ExplicitDataDirectory(["--data-dir"," \t "]),"whitespace data-dir cannot bypass the context guard");
Reject(()=>WorkspaceIdentity.ExplicitDataDirectory(["--data-dir",absolute,"--data-dir",absolute]),"duplicate identical data-dir arguments are rejected");
Reject(()=>WorkspaceIdentity.ExplicitDataDirectory(["--data-dir",absolute,"--data-dir","other-fixture"]),"duplicate conflicting data-dir arguments are rejected");
Reject(()=>WorkspaceIdentity.ExplicitDataDirectory(["--data-dir","bad\0path"]),"invalid path syntax never selects the implicit directory");
var validExplicit=WorkspaceIdentity.ExplicitDataDirectory(["--data-dir",absolute]);
Check(WorkspaceIdentity.StartupError(validExplicit is not null,"Foreign.Package") is null,"only a successfully parsed explicit directory enables packaged development startup");
var missingExplicit=WorkspaceIdentity.ExplicitDataDirectory(["--no-browser"]);
Check(WorkspaceIdentity.StartupError(missingExplicit is not null,"Foreign.Package") is not null,"absent explicit path retains packaged-context protection");

const string fingerprint="a1b2c3d4e5f67890"+"0123456789abcdef"+"0123456789abcdef"+"0123456789abcdef";
Check(WorkspaceIdentity.ConnectionCode(fingerprint)=="A1B2-C3D4-E5F6","connection code has a short readable certificate identity");
Check(WorkspaceIdentity.ConnectionCode(fingerprint.ToUpperInvariant())==WorkspaceIdentity.ConnectionCode(fingerprint),"fingerprint case cannot change the public identity");
Check(WorkspaceIdentity.ConnectionCode("F"+fingerprint[1..])!=WorkspaceIdentity.ConnectionCode(fingerprint),"different leading certificate fingerprints distinguish displayed PC codes");
Reject(()=>WorkspaceIdentity.ConnectionCode("short"),"short fingerprint is rejected");
Reject(()=>WorkspaceIdentity.ConnectionCode(new string('Z',64)),"non-hex fingerprint is rejected");
Reject(()=>WorkspaceIdentity.ConnectionCode(null!),"missing fingerprint is rejected");
Console.WriteLine(passed+" workspace identity checks passed; no configuration or device control was used.");
