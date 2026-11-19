"""Serve a real Git smart-HTTP request in the isolated HTTPS fixtures."""
import ssl
import subprocess
import urllib.parse


def serve_git_backend(handler, project, env, body, errors):
    parsed = urllib.parse.urlsplit(handler.path)
    cgi = dict(env,GIT_PROJECT_ROOT=project.as_posix(),GIT_HTTP_EXPORT_ALL='1',
        REQUEST_METHOD=handler.command,PATH_INFO=urllib.parse.unquote(parsed.path),
        QUERY_STRING=parsed.query,CONTENT_TYPE=handler.headers.get('Content-Type',''),
        CONTENT_LENGTH=str(len(body)),REMOTE_USER='alice',REMOTE_ADDR='127.0.0.1',
        SERVER_PROTOCOL='HTTP/1.1',SERVER_NAME='127.0.0.1',SERVER_PORT=str(handler.server.server_port),HTTPS='on')
    if handler.headers.get('Git-Protocol'):
        cgi['HTTP_GIT_PROTOCOL'] = handler.headers['Git-Protocol']
    response = subprocess.run(['git','http-backend'],input=body,env=cgi,cwd=project,
                              capture_output=True,timeout=30)
    headers,separator,data = response.stdout.partition(b'\r\n\r\n')
    if not separator:
        headers,separator,data = response.stdout.partition(b'\n\n')
    if response.returncode or not separator:
        errors.append(response.stderr.decode('utf-8','replace'))
        handler.send_error(500)
        return
    fields = [line.split(':',1) for line in headers.decode('ascii').splitlines() if ':' in line]
    status = next((int(value.strip().split()[0]) for name,value in fields if name.lower()=='status'),200)
    handler.send_response(status)
    for name,value in fields:
        if name.lower() not in ['status','content-length','connection','transfer-encoding']:
            handler.send_header(name,value.strip())
    handler.send_header('Content-Length',str(len(data)))
    handler.end_headers()
    try:handler.wfile.write(data)
    except (BrokenPipeError,ConnectionResetError,ssl.SSLError):pass
