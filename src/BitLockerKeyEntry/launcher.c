// Compiled main executable for "BitLocker Key Entry.app".
//
// The app's logic is the bash script Contents/Resources/main.sh. macOS privacy controls (TCC,
// "Removable Volumes") silently deny an app whose main executable is a script, so writing key.txt
// to the USB stick fails with "Operation not permitted". A signed Mach-O main executable gets a
// proper TCC identity (and prompt); the bash it execs inherits that identity.
#include <libgen.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

int main(void) {
    char exe[PATH_MAX], real[PATH_MAX], script[PATH_MAX];
    uint32_t n = sizeof exe;
    if (_NSGetExecutablePath(exe, &n) != 0 || !realpath(exe, real)) return 1;
    snprintf(script, sizeof script, "%s/../Resources/main.sh", dirname(real));
    char *args[] = {"/bin/bash", script, NULL};
    execv("/bin/bash", args);
    perror("execv /bin/bash");
    return 1;
}
