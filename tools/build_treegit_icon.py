"""Build TreeGit's original vector tree mark and Windows icon sizes."""
from pathlib import Path
from PIL import Image, ImageDraw

root=Path(__file__).resolve().parents[1]
assets=root/'build/resources/_common/icons'
colors={'background':'#103F47','trunk':'#F3FCF7','crown':'#75E7B5','left':'#22B99A','right':'#68CEE7'}
leaves=[('left',[(73,85),(118,130),(73,175),(28,130)]),('right',[(183,85),(228,130),(183,175),(138,130)]),('crown',[(128,36),(178,86),(128,136),(78,86)])]
svg=['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">',f'<rect x="8" y="8" width="240" height="240" rx="46" fill="{colors["background"]}"/>',f'<rect x="118" y="124" width="20" height="88" rx="7" fill="{colors["trunk"]}"/>']
for name,points in leaves:
    svg.append(f'<polygon points="'+ ' '.join(f'{x},{y}' for x,y in points) +f'" fill="{colors[name]}"/>')
svg.append('</svg>')
(assets/'treegit.svg').write_text('\n'.join(svg)+'\n',encoding='utf-8')
scale=4
canvas=Image.new('RGBA',(256*scale,256*scale),(0,0,0,0))
draw=ImageDraw.Draw(canvas)
draw.rounded_rectangle((8*scale,8*scale,248*scale,248*scale),radius=46*scale,fill=colors['background'])
draw.rounded_rectangle((118*scale,124*scale,138*scale,212*scale),radius=7*scale,fill=colors['trunk'])
for name,points in leaves:draw.polygon([(x*scale,y*scale) for x,y in points],fill=colors[name])
png=canvas.resize((512,512),Image.Resampling.LANCZOS)
png.save(assets/'treegit.png')
png.save(assets/'sourcegit.png') # Existing upstream packaging path, TreeGit artwork.
png.save(root/'src/Resources/Images/TreeGit.png')
canvas.resize((256,256),Image.Resampling.LANCZOS).save(root/'src/App.ico',format='ICO',sizes=[(n,n) for n in [16,20,24,32,40,48,64,128,256]])
print('TreeGit vector, PNG and multi-resolution Windows icon generated.')
