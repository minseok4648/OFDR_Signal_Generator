"""Exact coordinate/density conversion for fixed recorded native FFT coefficients."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import numpy as np

def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def write(p,v): p.write_text(json.dumps(v,ensure_ascii=False,indent=2,allow_nan=False),encoding='utf-8')

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--source',type=Path,required=True);ap.add_argument('--target',type=Path,required=True);a=ap.parse_args()
    old=read(a.source/'reflectogram_config.json');new=read(a.target/'reflectogram_config.json')
    ratio=new['AuxiliaryDelayMicroseconds']/old['AuxiliaryDelayMicroseconds'];records=[]
    for folder in sorted((a.source/'files').iterdir()):
        dest=a.target/'files'/folder.name;shutil.copytree(folder,dest,dirs_exist_ok=True)
        p=np.loadtxt(folder/'reflectogram.csv',delimiter=',',skiprows=1);q=p.copy()
        q[:,0]*=ratio;q[:,1]/=ratio;q[:,2]-=10*np.log10(ratio)
        np.savetxt(dest/'reflectogram.csv',q,delimiter=',',header='distance_m,power_V2_per_m,db_re_1V2_per_m',comments='',fmt='%.17g')
        m=read(dest/'manifest.json')
        for r in m['Ranges']:r['CorrectedNativeBinMeters']*=ratio
        write(dest/'manifest.json',m)
        before=float(np.trapezoid(p[:,1],p[:,0]));after=float(np.trapezoid(q[:,1],q[:,0]));relative=abs(after-before)/max(abs(before),1e-30)
        if relative>1e-12:raise RuntimeError('Integrated power changed')
        keep=(p[:,0]>=3.5)&(p[:,0]<=8.7)
        old_slope=float(np.polyfit(p[keep,0],p[keep,2],1)[0]);new_slope=float(np.polyfit(q[keep,0],q[keep,2],1)[0]);slope_error=abs(new_slope-old_slope/ratio)
        if slope_error>1e-10:raise RuntimeError('FFT slope did not follow exact coordinate scaling')
        records.append({'file_id':folder.name,'relative_integrated_power_error':relative,'same_bin_distance_fft_slope_old_db_per_m':old_slope,'same_bin_distance_fft_slope_new_db_per_m':new_slope,'slope_scaling_absolute_error':slope_error,'original_reflectogram_sha256':hashlib.sha256((folder/'reflectogram.csv').read_bytes()).hexdigest(),'calibrated_reflectogram_sha256':hashlib.sha256((dest/'reflectogram.csv').read_bytes()).hexdigest()})
    for name in ['input_inventory.json']:
        shutil.copy2(a.source/name,a.target/name)
    if (a.source/'validation').exists():shutil.copytree(a.source/'validation',a.target/'validation',dirs_exist_ok=True)
    write(a.target/'reflectogram_axis_calibration.json',{'source':str(a.source.resolve()),'target':str(a.target.resolve()),'confirmed_auxiliary_path_difference_m':200,'confirmed_measurement_length_m':23,'group_index':new['GroupIndex'],'group_index_provisional':True,'old_delay_us':old['AuxiliaryDelayMicroseconds'],'new_delay_us':new['AuxiliaryDelayMicroseconds'],'distance_scale_ratio':ratio,'method':'Keep the same native FFT coefficients and samples. z_new=r*z_old; PSD_new=PSD_old/r; dB_new=dB_old-10log10(r). No new interpolation, filtering, phase extraction, or FFT. Exact unit-coordinate conversion at stored samples.','records':records})
    print(json.dumps({'files':len(records),'ratio':ratio,'max_integrated_power_relative_error':max(r['relative_integrated_power_error'] for r in records)}))

if __name__=='__main__':main()
