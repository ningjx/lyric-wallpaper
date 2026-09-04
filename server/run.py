# -*- coding: utf-8 -*-
"""便捷启动入口：在 server/ 目录内 `python run.py`。

等价于在仓库根目录运行 `python -m server`。
server 是 Python 包，`python -m server` 需在父目录（仓库根）执行，
本脚本只是把仓库根加进 sys.path 再调用同一入口，方便沿用旧习惯。
"""
import os
import sys

_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ROOT not in sys.path:
    sys.path.insert(0, _ROOT)

from server.server import main  # noqa: E402

if __name__ == "__main__":
    main()