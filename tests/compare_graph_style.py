"""Compare an independently rendered graph fixture with local reference data."""
import json
from pathlib import Path
import sys
from PIL import Image, ImageDraw, ImageFont

folder=Path(sys.argv[1]).resolve()
reference_path=Path(sys.argv[2]).resolve() if len(sys.argv)>2 else folder/'reference-component-graph.json'
reference_image=Path(sys.argv[3]).resolve() if len(sys.argv)>3 else folder/'reference-component-graph.png'
reference=json.loads(reference_path.read_text(encoding='utf-8-sig'))
actual=json.loads((folder/'treegit-component-graph.png.json').read_text(encoding='utf-8-sig'))
assert len(reference)==len(actual)==5
for expected,result in zip(reference,actual):
    assert (expected['sha'],expected['x'],expected['y'])==(result['sha'],result['x'],result['y'])
    assert expected['color'] & 0xffffffff == int(result['color'].lstrip('#'),16)
a=Image.open(reference_image).convert('RGB')
b=Image.open(folder/'treegit-component-graph.png').convert('RGB')
different=sum(x!=y for x,y in zip(a.get_flattened_data(),b.get_flattened_data()))
comparison=Image.new('RGB',(640,440),'white')
comparison.paste(a.resize((320,380),Image.Resampling.NEAREST),(0,48))
comparison.paste(b.resize((320,380),Image.Resampling.NEAREST),(320,48))
draw=ImageDraw.Draw(comparison)
font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',16)
draw.text((12,12),'UI reference (4x)',fill='#172B4D',font=font)
draw.text((332,12),'TreeGit (4x)',fill='#172B4D',font=font)
comparison.save(folder/'graph-comparison.png')
result={'sample':'isolated five-commit merge; not a desktop screenshot','matched_nodes':5,'coordinates_match':True,'colors_match':True,'lane_spacing':11,'row_height':19,'line_width':2,'node_radius':4,'node_outline_width':1,'different_raster_pixels':different,'pixel_identical':different==0,'note':'WPF and Skia rasterization have edge antialiasing differences.'}
(folder/'graph-style-comparison.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result,indent=2))
