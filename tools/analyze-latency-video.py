#!/usr/bin/env python3
"""
WindowStream Latency Video Analyzer
===================================
Performs OCR analysis on latency-clock screen recordings to calculate
end-to-end frame-level latency between the host clock (bottom source) and
the GXR virtual spatial panel clock (top decoded).

Uses EasyOCR for text recognition and applies a temporal smoothing filter
to correct any individual digit OCR misclassifications (like 3999 -> 3990).

Requirements:
  pip install easyocr opencv-python numpy tqdm pandas

Usage:
  python tools/analyze-latency-video.py <video_path> [--step 5] [--output-csv latency_results.csv]
"""

import argparse
import os
import re
import sys
import numpy as np
import pandas as pd
import cv2
import easyocr
from tqdm import tqdm

def parse_args():
    parser = argparse.ArgumentParser(description="Analyze WindowStream Latency screen recording.")
    parser.add_argument("video_path", type=str, help="Path to the .mp4 screen recording.")
    parser.add_argument("--step", type=int, default=5, help="Process every Nth frame to speed up analysis. Default 5.")
    parser.add_argument("--clock-rate", type=float, default=165.0, help="Target frame rate cap of the latency clock. Default 165.0.")
    parser.add_argument("--output-csv", type=str, default=None, help="Save raw frame results to this CSV path.")
    return parser.parse_args()

def extract_frame_number(text_results):
    """
    Looks for the frame count pattern (usually 3 or 4 digits) in the OCR results.
    Filters out the word 'FRAME' or colons. Returns a tuple (frame_number, probability) or (None, 0.0).
    """
    candidates = []
    for bbox, text, prob in text_results:
        # Clean text: keep only alphanumeric characters
        cleaned = re.sub(r'[^a-zA-Z0-9]', '', text)
        
        # If the cleaned text is pure digits and has length 3 to 5
        if cleaned.isdigit() and 3 <= len(cleaned) <= 5:
            candidates.append((int(cleaned), prob))
            
    if candidates:
        # Return the candidate with the highest probability
        candidates = sorted(candidates, key=lambda x: x[1], reverse=True)
        return candidates[0]
    return (None, 0.0)

def smooth_sequence(frames, probs, timestamps, clock_rate=165.0):
    """
    Applies a robust median-offset temporal filter using the known physical
    clock rate (165 FPS). This completely eliminates digit misclassifications (like 3999->3990)
    and initial/ending blank frames by using time differences to calculate expected increments.
    """
    import math
    offsets = []
    for f, p, t in zip(frames, probs, timestamps):
        if f is not None and not (isinstance(f, float) and math.isnan(f)) and p > 0.70:
            offsets.append(f - t * clock_rate)
            
    if not offsets:
        # Fallback to lower probability threshold if no high-prob matches
        for f, p, t in zip(frames, probs, timestamps):
            if f is not None and not (isinstance(f, float) and math.isnan(f)):
                offsets.append(f - t * clock_rate)
                
    if not offsets:
        # If the entire video has no valid OCR frames in this half, return all None
        return [None] * len(frames)
        
    median_offset = np.median(offsets)
    
    # Generate expected frame number for every timestamp, using raw values if they are close
    # to expected to preserve real pipeline jitter/fluctuations, and falling back to 
    # expected progression for OCR misclassifications or missing frames.
    smoothed = []
    for f, p, t in zip(frames, probs, timestamps):
        expected = t * clock_rate + median_offset
        if f is not None and not (isinstance(f, float) and math.isnan(f)) and p > 0.60:
            # If the raw value is close to expected, keep it to preserve real physical variations!
            if abs(f - expected) <= 12:
                smoothed.append(int(round(f)))
                continue
        # Fallback to expected progression
        smoothed.append(int(round(expected)))
    return smoothed

def main():
    args = parse_args()
    
    if not os.path.exists(args.video_path):
        print(f"Error: Video file not found at {args.video_path}", file=sys.stderr)
        return 1
        
    # Initialize EasyOCR
    print("Initializing EasyOCR (CPU mode)...")
    reader = easyocr.Reader(['en'], gpu=False)
    
    cap = cv2.VideoCapture(args.video_path)
    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    fps = cap.get(cv2.CAP_PROP_FPS)
    duration = total_frames / fps if fps > 0 else 0
    
    print(f"Video Info:")
    print(f"  Path:     {args.video_path}")
    print(f"  Total:    {total_frames} frames")
    print(f"  FPS:      {fps:.2f}")
    print(f"  Duration: {duration:.2f} seconds")
    print(f"  Analyzing every {args.step}th frame (Step={args.step})")
    print()
    
    raw_results = []
    
    # Wrap with tqdm progress bar
    pbar = tqdm(total=total_frames // args.step, desc="OCR Analysis")
    
    frame_idx = 0
    processed_count = 0
    
    while cap.isOpened():
        ret, frame = cap.read()
        if not ret:
            break
            
        if frame_idx % args.step == 0:
            h, w, _ = frame.shape
            
            # Split into top (floating spatial panel / physical monitor) and bottom halves
            top_half = frame[0:h//2, :]
            bottom_half = frame[h//2:h, :]
            
            # To improve OCR speed and accuracy, we crop to the center third where clocks are
            cx0 = w // 4
            cx1 = 3 * w // 4
            
            top_clock_area = top_half[:, cx0:cx1]
            bottom_clock_area = bottom_half[:, cx0:cx1]
            
            # Run OCR on both regions
            top_ocr = reader.readtext(top_clock_area)
            bottom_ocr = reader.readtext(bottom_clock_area)
            
            top_val, top_prob = extract_frame_number(top_ocr)
            bottom_val, bottom_prob = extract_frame_number(bottom_ocr)
            
            raw_results.append({
                "video_frame": frame_idx,
                "timestamp_sec": frame_idx / fps if fps > 0 else 0,
                "top_frame_raw": top_val,
                "top_prob": top_prob,
                "bottom_frame_raw": bottom_val,
                "bottom_prob": bottom_prob
            })
            
            pbar.update(1)
            processed_count += 1
            
        frame_idx += 1
        
    cap.release()
    pbar.close()
    
    if not raw_results:
        print("Error: No frames processed.", file=sys.stderr)
        return 1
        
    df = pd.DataFrame(raw_results)
    
    # Smooth raw outputs using temporal consistency
    print("\nApplying temporal smoothing & outlier correction...")
    df["top_frame"] = smooth_sequence(df["top_frame_raw"].tolist(), df["top_prob"].tolist(), df["timestamp_sec"].tolist(), clock_rate=args.clock_rate)
    df["bottom_frame"] = smooth_sequence(df["bottom_frame_raw"].tolist(), df["bottom_prob"].tolist(), df["timestamp_sec"].tolist(), clock_rate=args.clock_rate)
    
    # Calculate latency in frames (Bottom Source - Top Decoded)
    df["latency_frames"] = df["bottom_frame"] - df["top_frame"]
    
    # Calculate latency in milliseconds
    latency_clock_fps = args.clock_rate
    df["latency_ms"] = df["latency_frames"] * (1000.0 / latency_clock_fps)
    
    # Save CSV if requested
    if args.output_csv:
        df.to_csv(args.output_csv, index=False)
        print(f"Saved raw frame results to {args.output_csv}")
        
    # Generate final statistics
    # In a real test, latency is small (usually under a few dozen frames). 
    # Large differences (e.g. > 100 frames / 600 ms) are obvious OCR classification/alignment 
    # errors, typically occurring at the start before the headset is worn or when looking away.
    # Filter them out to keep the statistics highly accurate.
    filtered_df = df[df["latency_frames"].abs() <= 100].copy()
    
    valid_latencies = filtered_df["latency_frames"].dropna().tolist()
    valid_latencies_ms = filtered_df["latency_ms"].dropna().tolist()
    
    if not valid_latencies:
        print("Warning: No matching/valid frames in the plausible latency range (<100 frames).")
        # Fall back to all data if none is in the filtered range
        valid_latencies = df["latency_frames"].dropna().tolist()
        valid_latencies_ms = df["latency_ms"].dropna().tolist()
        
    if not valid_latencies:
        print("Error: Could not calculate latency (no matching frames decoded).")
        return 1
        
    p50 = np.percentile(valid_latencies, 50)
    p95 = np.percentile(valid_latencies, 95)
    p50_ms = np.percentile(valid_latencies_ms, 50)
    p95_ms = np.percentile(valid_latencies_ms, 95)
    
    print("\nLatency Analysis Report")
    print("=======================")
    print(f"Processed:           {processed_count} frames")
    print(f"Source Clock Rate:   {latency_clock_fps} FPS (~{1000.0/latency_clock_fps:.2f} ms per frame)")
    print()
    print("Latency Metrics:")
    print(f"  p50 (Median):      {p50:.1f} frames ({p50_ms:.1f} ms)")
    print(f"  p95:               {p95:.1f} frames ({p95_ms:.1f} ms)")
    print(f"  Min:               {min(valid_latencies):.1f} frames ({min(valid_latencies_ms):.1f} ms)")
    print(f"  Max:               {max(valid_latencies):.1f} frames ({max(valid_latencies_ms):.1f} ms)")
    print(f"  Mean:              {np.mean(valid_latencies):.1f} frames ({np.mean(valid_latencies_ms):.1f} ms)")
    print(f"  Std Dev:           {np.std(valid_latencies):.1f} frames ({np.std(valid_latencies_ms):.1f} ms)")
    print()
    
    # Print a small timeline sample
    print("Sample Timeline (first 10 analyzed frames):")
    print(f"  {'Timestamp':>10}  {'Source (Bottom)':>15}  {'Decoded (Top)':>15}  {'Latency (Frames)':>18}  {'Latency (ms)':>12}")
    for _, row in df.head(10).iterrows():
        b_val = f"{int(row['bottom_frame'])}" if not pd.isna(row['bottom_frame']) else "-"
        t_val = f"{int(row['top_frame'])}" if not pd.isna(row['top_frame']) else "-"
        lat_val = f"{int(row['latency_frames'])}" if not pd.isna(row['latency_frames']) else "-"
        lat_ms_val = f"{row['latency_ms']:.1f}ms" if not pd.isna(row['latency_ms']) else "-"
        print(f"  {row['timestamp_sec']:9.2f}s  {b_val:>15s}  {t_val:>15s}  {lat_val:>18s}  {lat_ms_val:>12s}")
        
    return 0

if __name__ == "__main__":
    sys.exit(main())
