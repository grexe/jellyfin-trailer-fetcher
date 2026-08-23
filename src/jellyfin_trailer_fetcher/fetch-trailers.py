#!/usr/bin/env python3
import sys
import os

# Add src to sys.path if running directly
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from jellyfin_trailer_fetcher.fetch_trailers import main

if __name__ == "__main__":
    main()
