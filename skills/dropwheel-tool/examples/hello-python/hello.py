import os
import sys
from datetime import datetime

source, output = sys.argv[1], sys.argv[2]
print("progress 0.2", flush=True)
stat = os.stat(source)
lines = [
    f"name: {os.path.basename(source)}",
    f"size: {stat.st_size} bytes",
    f"modified: {datetime.fromtimestamp(stat.st_mtime):%Y-%m-%d %H:%M:%S}",
]
with open(output, "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")
print(f"detail {stat.st_size} bytes", flush=True)
print("progress 1", flush=True)
