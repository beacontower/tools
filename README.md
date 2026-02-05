# BeaconTower Tools

Developer tools from BeaconTower. Download binaries from [Releases](https://github.com/beacontower/tools/releases).

## Available Tools

### btscript

BtScript compiler - compile reactive dataflow definitions to C#/.NET.

```bash
# Download (Linux x64)
curl -L https://github.com/beacontower/tools/releases/latest/download/btscript-linux-x64 -o btscript
chmod +x btscript

# Usage
btscript compile flow.scm      # Compile to C#
btscript check flow.scm        # Syntax check
btscript format flow.scm       # Pretty-print
btscript --help                # All commands
```

**Platforms:** linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64

## Issues

Report issues for any tool in this repository's [Issues](https://github.com/beacontower/tools/issues).

Use labels to categorize:
- `btscript` - BtScript compiler issues
- `bug` - Something isn't working
- `enhancement` - Feature requests

## License

Proprietary - BeaconTower AB
