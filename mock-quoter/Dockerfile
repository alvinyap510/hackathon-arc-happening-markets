# mock-quoter — external RFM quoter agent. Plain Node, no build step.
FROM node:20-alpine
WORKDIR /app
COPY package.json index.js ./
ENV NODE_ENV=production
CMD ["node", "index.js"]