function update() {
  // process is a global Node.js object
  return process.version;
}

module.exports = {
  update
};