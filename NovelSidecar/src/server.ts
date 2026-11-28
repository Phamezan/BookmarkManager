import { createServer } from './app';

const port = Number.parseInt(process.env.PORT || '3000', 10);
const host = process.env.HOST || '0.0.0.0';

const server = createServer();
server.listen(port, host, () => {
  console.log(`NovelSidecar listening on http://${host}:${port}`);
});

export { createServer };
