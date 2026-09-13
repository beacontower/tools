# BeaconTower Tools

Developer tools from BeaconTower. Download binaries from [Releases](https://github.com/beacontower/tools/releases).

## Available Tools

### btscript

BtScript compiler - compile reactive dataflow definitions to C#/.NET.

```bash
# Download (Linux x64). This repo hosts several tools and GitHub's "latest"
# release is shared between them, so resolve the newest btscript tag first.
tag=$(curl -s https://api.github.com/repos/beacontower/tools/releases \
  | grep -o '"tag_name": *"btscript-[^"]*"' | head -1 | cut -d'"' -f4)
curl -L https://github.com/beacontower/tools/releases/download/$tag/btscript-linux-x64 -o btscript
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
