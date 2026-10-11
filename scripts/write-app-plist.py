#!/usr/bin/env python3
import pathlib, plistlib, sys
app, identifier, version, build, demo = sys.argv[1:]
info = dict(CFBundleExecutable='Vemo', CFBundleIdentifier=identifier,
            CFBundleName='Vemo', CFBundleDisplayName='Vemo',
            CFBundleIconFile='Vemo.icns', CFBundlePackageType='APPL',
            CFBundleShortVersionString=version, CFBundleVersion=build,
            LSMinimumSystemVersion='26.2', LSMultipleInstancesProhibited=True,
            NSHighResolutionCapable=True, NSHumanReadableCopyright='Copyright 2026 SANGDNOG',
            VemoDemoMode=demo == '1')
with (pathlib.Path(app)/'Contents/Info.plist').open('wb') as output:
    plistlib.dump(info, output)
