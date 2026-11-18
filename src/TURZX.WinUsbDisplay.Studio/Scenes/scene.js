// 用最小时间间隔限制重绘频率，并向场景传递秒数。
function animateScene(render, minimumInterval = 31) {
  let last = -1;
  // 在达到帧间隔时渲染，并限制单帧推进的时间。
  function step(now) {
    if (last < 0 || now - last >= minimumInterval) {
      render(now / 1000, last < 0 ? 0 : Math.min((now - last) / 1000, .06));
      last = now;
    }
    requestAnimationFrame(step);
  }
  requestAnimationFrame(step);
}

// 仅在视口尺寸改变时重设画布的像素尺寸。
function resizeScene(canvas) {
  if (canvas.width !== innerWidth || canvas.height !== innerHeight) {
    canvas.width = innerWidth;
    canvas.height = innerHeight;
  }
}
