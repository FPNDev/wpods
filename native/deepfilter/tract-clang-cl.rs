// C compiler for tract-linalg's Arm64 assembly kernels on Windows (built and used by build-deepfilter.ps1 -Arch arm64).
//
// For MSVC targets, the build script of tract-linalg 0.19 rewrites the '//' comments of its GNU-syntax .S kernels as
// ';', which suits Microsoft's assemblers. clang-cl, the compiler that assembles GNU syntax for Windows on Arm64, reads
// ';' as a statement separator instead, so every comment turns into code. The kernel templates have no ';' of their
// own, so turning each ';' of a .S input back into '//' restores the kernels exactly.
// The cc crate (1.0.83) also leaves out -c when it assembles with clang-cl, so clang-cl would link the object; -c is
// added for .S inputs. Then clang-cl (path in WPODS_CLANG_CL) runs with the other arguments unchanged. Inputs that
// are not .S files pass through as is.
use std::{env, ffi::OsString, fs, path::Path, process};

fn main() {
    let clang = env::var_os("WPODS_CLANG_CL").expect("WPODS_CLANG_CL is not set");
    let mut args: Vec<OsString> = env::args_os().skip(1).collect();
    let mut assembly = false;
    for arg in &args {
        let path = Path::new(arg);
        if path.extension().is_some_and(|e| e == "S") {
            assembly = true;
            let text = fs::read_to_string(path).expect("cannot read the .S input");
            if text.contains(';') {
                fs::write(path, text.replace(';', "//")).expect("cannot write the .S input");
            }
        }
    }
    if assembly && !args.iter().any(|a| a == "-c" || a == "/c") {
        args.insert(0, "-c".into());
    }
    let status = process::Command::new(clang).args(&args).status().expect("cannot run clang-cl");
    process::exit(status.code().unwrap_or(1));
}
